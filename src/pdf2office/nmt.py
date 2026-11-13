"""Pure-NumPy inference for Argos Translate models (CTranslate2 TransformerSpec).

Argos Translate ships Transformer models in the CTranslate2 ``model.bin``
format: pre-norm encoder/decoder, fused attention projections, int8 weights
with one float scale per output row, shared embeddings and sinusoidal
positions. This module reads that file and runs beam search with NumPy only,
so the portable build needs no CTranslate2 binary (whose Windows wheel links
proprietary Intel MKL/OpenMP components).

The decoder weights are dequantized once; the larger encoder stays int8 in
memory and is dequantized layer by layer for each batch of sentences.
"""
from __future__ import annotations

from dataclasses import dataclass
import json
import math
from pathlib import Path
import struct

import numpy as np

_DTYPES = {0: np.float32, 1: np.int8, 2: np.int16, 3: np.int32, 4: np.float16}


def load_variables(path: Path) -> dict[str, np.ndarray]:
    """Parse a CTranslate2 model.bin (binary format versions 3-6)."""
    data = memoryview(Path(path).read_bytes())
    offset = 0

    def take(fmt: str):
        nonlocal offset
        values = struct.unpack_from("<" + fmt, data, offset)
        offset += struct.calcsize("<" + fmt)
        return values

    def text() -> str:
        nonlocal offset
        (length,) = take("H")
        value = bytes(data[offset:offset + length]).decode("utf-8").rstrip("\x00")
        offset += length
        return value

    (version,) = take("I")
    if not 3 <= version <= 6:
        raise ValueError(f"Неподдерживаемая версия модели перевода: {version}")
    spec = text()
    take("I")  # spec revision
    if spec != "TransformerSpec":
        raise ValueError(f"Неподдерживаемая архитектура модели перевода: {spec}")
    (count,) = take("I")
    variables: dict[str, np.ndarray] = {}
    for _ in range(count):
        name = text()
        (rank,) = take("B")
        dims = take("I" * rank) if rank else ()
        if version >= 4:
            (type_id,) = take("B")
            (size,) = take("I")
        else:
            type_id, size = 0, None
        dtype = np.dtype(_DTYPES[type_id])
        if size is None:
            size = int(np.prod(dims, dtype=np.int64)) * dtype.itemsize
        array = np.frombuffer(data, dtype=dtype, count=size // dtype.itemsize, offset=offset)
        offset += size
        variables[name] = array.reshape(dims) if rank else array[0]
    (aliases,) = take("I")
    for _ in range(aliases):
        alias, target = text(), text()
        variables[alias] = variables[target]
    return variables


def sinusoidal_positions(length: int, depth: int) -> np.ndarray:
    """CTranslate2's encoding: positions start at 1, [sin | cos] halves."""
    increment = math.log(10000.0) / (depth // 2 - 1)
    timescales = np.exp(np.arange(depth // 2, dtype=np.float32) * -np.float32(increment))
    scaled = np.arange(1, length + 1, dtype=np.float32)[:, None] * timescales[None, :]
    return np.concatenate([np.sin(scaled), np.cos(scaled)], axis=1).astype(np.float32)


def _dequantize(variables: dict, name: str) -> np.ndarray:
    weight = variables[name]
    scale = variables.get(name + "_scale")
    if weight.dtype == np.int8 and scale is not None:
        return weight.astype(np.float32) / np.asarray(scale, np.float32)[:, None]
    return np.asarray(weight, np.float32)


def _optional(variables: dict, name: str) -> np.ndarray | None:
    value = variables.get(name)
    return None if value is None else np.asarray(value, np.float32)


def _layer_norm(x: np.ndarray, gamma: np.ndarray, beta: np.ndarray, epsilon: float = 1e-6) -> np.ndarray:
    mean = x.mean(axis=-1, keepdims=True)
    variance = ((x - mean) ** 2).mean(axis=-1, keepdims=True)
    return (x - mean) / np.sqrt(variance + epsilon) * gamma + beta


def _softmax(x: np.ndarray) -> np.ndarray:
    x = x - x.max(axis=-1, keepdims=True)
    np.exp(x, out=x)
    x /= x.sum(axis=-1, keepdims=True)
    return x


def _heads(x: np.ndarray, heads: int) -> np.ndarray:
    batch, length, width = x.shape
    return x.reshape(batch, length, heads, width // heads).transpose(0, 2, 1, 3)


def _merge(x: np.ndarray) -> np.ndarray:
    batch, heads, length, depth = x.shape
    return x.transpose(0, 2, 1, 3).reshape(batch, length, heads * depth)


def _relative_positions(queries: int, keys: int, maximum: int) -> np.ndarray:
    """CTranslate2 make_relative_positions: clip(j - (i + offset)) + maximum."""
    offset = keys - queries
    i = np.arange(queries)[:, None]
    j = np.arange(keys)[None, :]
    return np.clip(j - (i + offset), -maximum, maximum) + maximum


def _attention(query, key, value, heads, mask=None, relative_keys=None, relative_values=None):
    q, k, v = _heads(query, heads), _heads(key, heads), _heads(value, heads)
    scores = q @ k.transpose(0, 1, 3, 2)
    positions = None
    if relative_keys is not None:
        # Self-attention with relative position representations (Shaw et al.).
        positions = _relative_positions(q.shape[2], k.shape[2], (relative_keys.shape[0] - 1) // 2)
        scores = scores + np.einsum("bhid,ijd->bhij", q, relative_keys[positions], optimize=True)
    scores = scores / np.float32(math.sqrt(q.shape[-1]))
    if mask is not None:
        scores = np.where(mask, scores, np.float32(-1e9))
    weights = _softmax(scores)
    output = weights @ v
    if relative_values is not None:
        if positions is None:
            positions = _relative_positions(q.shape[2], k.shape[2], (relative_values.shape[0] - 1) // 2)
        output = output + np.einsum("bhij,ijd->bhid", weights, relative_values[positions], optimize=True)
    return _merge(output)


@dataclass
class _DecoderLayer:
    self_norm: tuple
    self_qkv: np.ndarray
    self_out: np.ndarray
    cross_norm: tuple
    cross_q: np.ndarray
    cross_kv: np.ndarray
    cross_out: np.ndarray
    ffn_norm: tuple
    ffn_in: np.ndarray
    ffn_out: np.ndarray
    relative_keys: np.ndarray | None = None
    relative_values: np.ndarray | None = None


class TransformerModel:
    """Encoder-decoder Transformer from a CTranslate2 model directory."""

    def __init__(self, directory: Path):
        directory = Path(directory)
        self.variables = variables = load_variables(directory / "model.bin")
        config = json.loads((directory / "config.json").read_text(encoding="utf-8"))
        vocabulary = json.loads((directory / "shared_vocabulary.json").read_text(encoding="utf-8"))
        self.vocabulary = vocabulary
        self.index = {token: number for number, token in enumerate(vocabulary)}
        for flag in ("encoder/pre_norm", "decoder/pre_norm"):
            if int(variables.get(flag, 1)) != 1:
                raise ValueError("Поддерживаются только модели перевода с pre-norm.")
        if int(variables.get("decoder/alibi", 0)) or int(variables.get("encoder/activation", 0)) not in (0,):
            raise ValueError("Неподдерживаемые параметры модели перевода.")
        self.heads = int(variables["encoder/num_heads"])
        self.decoder_heads = int(variables["decoder/num_heads"])
        self.epsilon = float(config.get("layer_norm_epsilon") or 1e-6)
        self.embeddings = _dequantize(variables, "decoder/embeddings/weight")
        self.width = self.embeddings.shape[1]
        self.scale = math.sqrt(self.width) if int(variables.get("decoder/scale_embeddings", 1)) else 1.0
        self.source_scale = math.sqrt(self.width) if int(variables.get("encoder/scale_embeddings", 1)) else 1.0
        # Models with relative position representations have no absolute
        # position encoder (CTranslate2 TransformerEncoder/Decoder).
        self.decoder_relative = "decoder/layer_0/self_attention/relative_position_keys" in variables
        self.encoder_relative = "encoder/layer_0/self_attention/relative_position_keys" in variables
        stored = variables.get("decoder/position_encodings/encodings")
        self.positions = (None if self.decoder_relative else np.asarray(stored, np.float32) if stored is not None
                          else sinusoidal_positions(512, self.width))
        self.source_embeddings = (self.embeddings if "encoder/embeddings_0/weight" not in variables
                                  or variables["encoder/embeddings_0/weight"] is variables["decoder/embeddings/weight"]
                                  else _dequantize(variables, "encoder/embeddings_0/weight"))
        source_stored = variables.get("encoder/position_encodings/encodings")
        self.source_positions = (None if self.encoder_relative else np.asarray(source_stored, np.float32)
                                 if source_stored is not None else sinusoidal_positions(512, self.width))
        self.encoder_layers = sum(1 for name in variables if name.startswith("encoder/layer_") and name.endswith("/self_attention/linear_0/weight"))
        self.decoder = []
        number = 0
        while f"decoder/layer_{number}/self_attention/linear_0/weight" in variables:
            prefix = f"decoder/layer_{number}/"
            norm = lambda name: (np.asarray(variables[prefix + name + "/gamma"], np.float32),
                                 np.asarray(variables[prefix + name + "/beta"], np.float32))
            self.decoder.append(_DecoderLayer(
                norm("self_attention/layer_norm"),
                _dequantize(variables, prefix + "self_attention/linear_0/weight").T.copy(),
                _dequantize(variables, prefix + "self_attention/linear_1/weight").T.copy(),
                norm("attention/layer_norm"),
                _dequantize(variables, prefix + "attention/linear_0/weight").T.copy(),
                _dequantize(variables, prefix + "attention/linear_1/weight").T.copy(),
                _dequantize(variables, prefix + "attention/linear_2/weight").T.copy(),
                norm("ffn/layer_norm"),
                _dequantize(variables, prefix + "ffn/linear_0/weight").T.copy(),
                _dequantize(variables, prefix + "ffn/linear_1/weight").T.copy(),
                _optional(variables, prefix + "self_attention/relative_position_keys"),
                _optional(variables, prefix + "self_attention/relative_position_values"),
            ))
            number += 1
        self.final_norm = (np.asarray(variables["decoder/layer_norm/gamma"], np.float32),
                           np.asarray(variables["decoder/layer_norm/beta"], np.float32))
        self.projection_bias = np.asarray(variables.get("decoder/projection/bias", 0.0), np.float32)
        projection = variables.get("decoder/projection/weight")
        self.projection = (self.embeddings if projection is None or projection is variables["decoder/embeddings/weight"]
                           else _dequantize(variables, "decoder/projection/weight"))
        self.start = self.index[config.get("decoder_start_token", "<s>")]
        self.end = self.index[config.get("eos_token", "</s>")]
        self.unknown = self.index.get(config.get("unk_token", "<unk>"), 0)
        self.add_source_bos = bool(config.get("add_source_bos"))
        self.add_source_eos = bool(config.get("add_source_eos"))

    # ----------------------------------------------------------------- encoder
    def encode(self, sources: list[list[int]]) -> tuple[np.ndarray, np.ndarray]:
        length = max(len(source) for source in sources)
        ids = np.zeros((len(sources), length), np.int64)
        mask = np.zeros((len(sources), length), bool)
        for row, source in enumerate(sources):
            ids[row, :len(source)] = source
            mask[row, :len(source)] = True
        x = self.source_embeddings[ids] * self.source_scale
        if self.source_positions is not None:
            if length > len(self.source_positions):
                self.source_positions = sinusoidal_positions(length, self.width)
            x = x + self.source_positions[:length]
        attention_mask = mask[:, None, None, :]
        v = self.variables
        for number in range(self.encoder_layers):
            prefix = f"encoder/layer_{number}/"
            normed = _layer_norm(x, v[prefix + "self_attention/layer_norm/gamma"],
                                 v[prefix + "self_attention/layer_norm/beta"], self.epsilon)
            qkv = normed @ _dequantize(v, prefix + "self_attention/linear_0/weight").T
            q, k, w = np.split(qkv, 3, axis=-1)
            x = x + _attention(q, k, w, self.heads, attention_mask,
                               _optional(v, prefix + "self_attention/relative_position_keys"),
                               _optional(v, prefix + "self_attention/relative_position_values")) \
                @ _dequantize(v, prefix + "self_attention/linear_1/weight").T
            normed = _layer_norm(x, v[prefix + "ffn/layer_norm/gamma"], v[prefix + "ffn/layer_norm/beta"], self.epsilon)
            hidden = np.maximum(normed @ _dequantize(v, prefix + "ffn/linear_0/weight").T, 0)
            x = x + hidden @ _dequantize(v, prefix + "ffn/linear_1/weight").T
        x = _layer_norm(x, v["encoder/layer_norm/gamma"], v["encoder/layer_norm/beta"], self.epsilon)
        return x.astype(np.float32), mask

    # ----------------------------------------------------------------- decoder
    def _step(self, tokens: np.ndarray, position: int, caches: list, memory_kv: list, memory_mask: np.ndarray) -> np.ndarray:
        x = self.embeddings[tokens] * self.scale
        if self.positions is not None:
            x = x + self.positions[position]
        x = x[:, None, :]
        for layer, cache, (key_memory, value_memory) in zip(self.decoder, caches, memory_kv):
            normed = _layer_norm(x, *layer.self_norm, self.epsilon)
            q, k, w = np.split(normed @ layer.self_qkv, 3, axis=-1)
            cache[0] = k if cache[0] is None else np.concatenate([cache[0], k], axis=1)
            cache[1] = w if cache[1] is None else np.concatenate([cache[1], w], axis=1)
            x = x + _attention(q, cache[0], cache[1], self.decoder_heads, None,
                               layer.relative_keys, layer.relative_values) @ layer.self_out
            normed = _layer_norm(x, *layer.cross_norm, self.epsilon)
            x = x + _attention(normed @ layer.cross_q, key_memory, value_memory, self.decoder_heads,
                               memory_mask) @ layer.cross_out
            normed = _layer_norm(x, *layer.ffn_norm, self.epsilon)
            x = x + np.maximum(normed @ layer.ffn_in, 0) @ layer.ffn_out
        x = _layer_norm(x[:, 0, :], *self.final_norm, self.epsilon)
        logits = x @ self.projection.T + self.projection_bias
        logits = logits - logits.max(axis=-1, keepdims=True)
        return logits - np.log(np.exp(logits).sum(axis=-1, keepdims=True))

    def beam_search(self, memory: np.ndarray, mask: np.ndarray, *, beam_size: int = 4,
                    length_penalty: float = 0.2, max_length: int = 256) -> list[int]:
        """Translate one encoded sentence; returns target token ids without EOS.

        Mirrors CTranslate2's BeamSearch (2×beam candidates per step; an EOS
        among the first ``beam`` candidates finishes a hypothesis and its slot
        is refilled from the remaining candidates; the search ends after
        ``beam`` finished hypotheses; scores are divided by length**penalty,
        the length counting the EOS token).
        """
        if self.positions is not None and len(self.positions) < max_length + 1:
            self.positions = sinusoidal_positions(max_length + 1, self.width)
        memory = memory[None]
        kv = []
        for layer in self.decoder:
            key, value = np.split(memory @ layer.cross_kv, 2, axis=-1)
            kv.append((key, value))
        memory_mask = mask[None, None, None, :]
        caches = [[None, None] for _ in self.decoder]
        tokens = np.array([self.start], np.int64)
        scores = np.zeros(1, np.float32)
        histories: list[list[int]] = [[]]
        finished: list[tuple[float, list[int]]] = []
        expanded = False
        for step in range(max_length):
            log_probs = self._step(tokens, step, caches, kv, memory_mask)
            total = (scores[:, None] + log_probs).ravel()
            count = 2 * beam_size
            candidates = np.argpartition(-total, count)[:count]
            candidates = candidates[np.argsort(-total[candidates], kind="stable")]
            vocabulary = log_probs.shape[1]
            last_step = step + 1 == max_length
            slots = list(range(beam_size))
            secondary = beam_size
            for k in range(beam_size):
                beam, token = divmod(int(candidates[k]), vocabulary)
                if token == self.end or last_step:
                    # The translator keeps EOS in the hypothesis while scoring
                    # (include_eos_in_hypotheses) and strips it afterwards.
                    history = histories[beam] + [token]
                    finished.append((float(total[candidates[k]]), history))
                    for j in range(secondary, count):
                        if int(candidates[j]) % vocabulary != self.end:
                            slots[k] = j
                            secondary = j + 1
                            break
            if last_step or len(finished) >= beam_size:
                break
            chosen = [int(candidates[slot]) for slot in slots]
            order = np.array([candidate // vocabulary for candidate in chosen])
            next_tokens = [candidate % vocabulary for candidate in chosen]
            histories = [histories[beam] + [token] for beam, token in zip(order, next_tokens)]
            scores = np.array([total[candidate] for candidate in chosen], np.float32)
            tokens = np.array(next_tokens, np.int64)
            if not expanded:
                kv = [(np.repeat(key, beam_size, axis=0), np.repeat(value, beam_size, axis=0)) for key, value in kv]
                memory_mask = np.repeat(memory_mask, beam_size, axis=0)
                expanded = True
            for cache in caches:
                cache[0] = cache[0][order]
                cache[1] = cache[1][order]

        def normalized(item):
            score, history = item
            return score / (len(history) ** length_penalty) if history else -math.inf

        best = max(finished, key=normalized) if finished else (0.0, [])
        return [token for token in best[1] if token not in (self.unknown, self.end)]

    def translate_tokens(self, sources: list[list[str]], *, beam_size: int = 4,
                         length_penalty: float = 0.2) -> list[list[str]]:
        ids = []
        for tokens in sources:
            source = [self.index.get(token, self.unknown) for token in tokens]
            if self.add_source_bos:
                source.insert(0, self.start)
            if self.add_source_eos:
                source.append(self.end)
            ids.append(source or [self.end])
        results = []
        for start in range(0, len(ids), 16):
            batch = ids[start:start + 16]
            memory, mask = self.encode(batch)
            for row, source in enumerate(batch):
                length = len(source)
                output = self.beam_search(memory[row, :length], mask[row, :length], beam_size=beam_size,
                                          length_penalty=length_penalty, max_length=256)
                results.append([self.vocabulary[token] for token in output])
        return results
