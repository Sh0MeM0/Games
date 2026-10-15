/** Conservative, explicitly inferred repair of short interrupted strokes.
 *
 * This module never claims to recover hidden source pixels. It joins two
 * existing, aligned stroke ends only across an explicitly supplied removal
 * mask. The inferred pixels are returned separately, so callers can disclose
 * and disable this optional operation. It must run before a user's erase brush.
 */
const LIMIT = 24_000_000;
const clamp = (n, low, high) => Math.max(low, Math.min(high, n));
const offsets = [[0,-1],[1,-1],[1,0],[1,1],[0,1],[-1,1],[-1,0],[-1,-1]];

function validate(input) {
  const { width, height, mask, data, sourceMask, removedMask, ruleMask } = input;
  const size = width * height;
  if (!Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1 || size > LIMIT || mask?.length !== size)
    throw new Error('Некорректная маска для достройки штрихов.');
  for (const candidate of [sourceMask, removedMask, ruleMask])
    if (candidate && candidate.length !== size) throw new Error('Размер маски удаления не совпадает с изображением.');
  if (data && data.length !== size * 4) throw new Error('Размер изображения не совпадает с маской штрихов.');
}

export function skeletonize(mask, width, height) {
  // Padding protects strokes that touch a cropped region's boundary.
  const stride = width + 2, rows = height + 2, skeleton = new Uint8Array(stride * rows);
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) skeleton[(y + 1) * stride + x + 1] = mask[y * width + x] ? 1 : 0;
  const deltas = [-stride, -stride+1, 1, stride+1, stride, stride-1, -1, -stride-1];
  const pending = new Int32Array(mask.length);
  // Zhang-Suen thinning, capped for unusually thick shapes. Such shapes are
  // intentionally left without endpoints instead of aggressively simplified.
  for (let iteration = 0; iteration < 48; iteration++) {
    let changed = false;
    for (let phase = 0; phase < 2; phase++) {
      let count = 0;
      for (let y = 1; y <= height; y++) for (let x = 1; x <= width; x++) {
        const i = y * stride + x;
        if (!skeleton[i]) continue;
        let neighbors = 0, transitions = 0;
        for (let n = 0; n < 8; n++) {
          const value = skeleton[i + deltas[n]];
          neighbors += value;
          if (!value && skeleton[i + deltas[(n + 1) % 8]]) transitions++;
        }
        if (neighbors < 2 || neighbors > 6 || transitions !== 1) continue;
        const north = skeleton[i-stride], east = skeleton[i+1], south = skeleton[i+stride], west = skeleton[i-1];
        if (phase === 0 ? (north*east*south || east*south*west) : (north*east*west || north*south*west)) continue;
        pending[count++] = i;
      }
      if (count) changed = true;
      for (let n = 0; n < count; n++) skeleton[pending[n]] = 0;
    }
    if (!changed) break;
  }
  const result = new Uint8Array(mask.length);
  for (let y = 0; y < height; y++) result.set(skeleton.subarray((y+1)*stride+1, (y+1)*stride+1+width), y*width);
  return result;
}

function neighbors(index, mask, width, height) {
  const x = index % width, y = Math.floor(index / width), result = [];
  for (const [dx, dy] of offsets) {
    const nx = x + dx, ny = y + dy;
    if (nx < 0 || ny < 0 || nx >= width || ny >= height || !mask[ny * width + nx]) continue;
    // Diagonal links across a filled right-angle corner are redundant, and
    // otherwise make ordinary rasterized curves appear to have junctions.
    if (dx && dy && (mask[y * width + nx] || mask[ny * width + x])) continue;
    result.push(ny * width + nx);
  }
  return result;
}

function nearMask(mask, width, height, x, y, radius = 1) {
  for (let yy = Math.max(0, y-radius); yy <= Math.min(height-1, y+radius); yy++)
    for (let xx = Math.max(0, x-radius); xx <= Math.min(width-1, x+radius); xx++)
      if (mask[yy*width+xx]) return true;
  return false;
}

function endpoint(index, skeleton, original, width, height, minimumTrace) {
  const points = [index]; let previous = -1, current = index;
  for (let step = 0; step < 12; step++) {
    const next = neighbors(current, skeleton, width, height).filter(n => n !== previous);
    if (next.length !== 1 || points.includes(next[0])) break;
    previous = current; current = next[0]; points.push(current);
  }
  if (points.length < minimumTrace + 1) return null;
  const x = index % width, y = Math.floor(index / width);
  // Thinning leaves a short horizontal/vertical spur along the cap of a thick
  // oblique stroke. Fit the interior segment instead of mistaking that cap for
  // the pen's direction. Several measured pixels still constrain every bridge.
  const interior=points.slice(Math.min(4,points.length-4));
  let mx=0,my=0;
  for(const p of interior){mx+=p%width;my+=Math.floor(p/width);}
  mx/=interior.length;my/=interior.length;
  let xx=0,yy=0,xy=0;
  for(const p of interior){const px=p%width-mx,py=Math.floor(p/width)-my;xx+=px*px;yy+=py*py;xy+=px*py;}
  const angle=.5*Math.atan2(2*xy,xx-yy);
  let dx=Math.cos(angle),dy=Math.sin(angle);
  if(dx*(x-mx)+dy*(y-my)<0){dx=-dx;dy=-dy;}
  if(Math.hypot(x-mx,y-my)<3)return null;
  const widths = [];
  for (const p of points.slice(2, 6)) {
    const px = p % width, py = Math.floor(p/width); let thick = 1;
    for (const sign of [-1,1]) for (let distance = 1; distance <= 4; distance++) {
      const sx = Math.round(px-dy*distance*sign), sy = Math.round(py+dx*distance*sign);
      if (sx<0 || sy<0 || sx>=width || sy>=height || !original[sy*width+sx]) break;
      thick++;
    }
    widths.push(thick);
  }
  widths.sort((a,b)=>a-b);
  const strokeWidth = clamp(widths[Math.floor(widths.length/2)] || 1, 1, 5);
  // Continue that centerline only through already observed foreground to find
  // its true cap. This corrects the skeleton's sideways endpoint without
  // inventing any pixels during endpoint detection.
  let tip=index;
  for(let distance=0;distance<=24;distance+=.25){
    const nx=Math.round(mx+dx*distance),ny=Math.round(my+dy*distance);
    if(nx<0||ny<0||nx>=width||ny>=height||!original[ny*width+nx])break;
    tip=ny*width+nx;
  }
  if(strokeWidth===1||Math.hypot(tip%width-x,Math.floor(tip/width)-y)>strokeWidth*2+6)tip=index;
  return { index:tip, x:tip%width, y:Math.floor(tip/width), dx, dy, width:strokeWidth, colorIndex: points[Math.min(5,points.length-1)] };

}

function curve(a, b, distance) {
  const handle = Math.min(distance/3, 6), count = Math.ceil(distance*3), result = [];
  for (let step = 0; step <= count; step++) {
    const t = step/count, s = 1-t;
    const x = s*s*s*a.x + 3*s*s*t*(a.x+a.dx*handle) + 3*s*t*t*(b.x+b.dx*handle) + t*t*t*b.x;
    const y = s*s*s*a.y + 3*s*s*t*(a.y+a.dy*handle) + 3*s*t*t*(b.y+b.dy*handle) + t*t*t*b.y;
    result.push({x:Math.round(x), y:Math.round(y), t});
  }
  return result;
}

/**
 * @param {{width:number,height:number,mask:Uint8Array,data?:Uint8ClampedArray,
 * sourceMask?:Uint8Array,removedMask?:Uint8Array,ruleMask?:Uint8Array}} input
 * `removedMask` must describe deliberately erased pixels/regions, never a page-
 * wide unknown background. `ruleMask` can additionally mark form-rule regions
 * whose black pixels hid colored pen strokes before the color filter.
 * @param {{enabled?:boolean,maxGap?:number,minimumTrace?:number,minimumEvidence?:number}} options
 * @returns {{width:number,height:number,mask:Uint8Array,data?:Uint8ClampedArray,
 * inferredMask:Uint8Array,inferredPixels:number,bridges:number}}
 */
export function repairStrokes(input, options = {}) {
  validate(input);
  const {width,height,mask,data,sourceMask,removedMask,ruleMask} = input;
  const result = {width,height,mask:new Uint8Array(mask),inferredMask:new Uint8Array(mask.length),inferredPixels:0,bridges:0};
  if (data) result.data = new Uint8ClampedArray(data);
  if (options.enabled === false || (!removedMask && !ruleMask)) return result;
  const evidence = new Uint8Array(mask.length);
  let hasEvidence = false;
  for (let i = 0; i < mask.length; i++) {
    evidence[i] = !mask[i] && ((ruleMask?.[i]) || (removedMask?.[i] && (!sourceMask || sourceMask[i]))) ? 1 : 0;
    hasEvidence ||= Boolean(evidence[i]);
  }
  if (!hasEvidence) return result;
  const maxGap = clamp(Number(options.maxGap) || 12, 2, 24);
  const minimumTrace = clamp(Math.round(options.minimumTrace ?? 6), 4, 12);
  const minimumEvidence = clamp(Number(options.minimumEvidence) || .7, .5, 1);
  const skeleton = skeletonize(mask,width,height), ends = [];
  for (let index = 0; index < skeleton.length; index++) {
    if (!skeleton[index]) continue;
    const x=index%width,y=Math.floor(index/width);
    if (!nearMask(evidence,width,height,x,y,7) || neighbors(index,skeleton,width,height).length !== 1) continue;
    const value=endpoint(index,skeleton,mask,width,height,minimumTrace);
    if (value) ends.push(value);
  }
  // A spatial grid bounds pairing work even on pages containing many letters.
  const grid = new Map(), cellSize = maxGap+3;
  for (let n=0;n<ends.length;n++) {
    const key=`${Math.floor(ends[n].x/cellSize)},${Math.floor(ends[n].y/cellSize)}`;
    if (!grid.has(key)) grid.set(key,[]);
    grid.get(key).push(n);
  }
  const candidates = [], byEnd = Array.from({length:ends.length},()=>[]);
  for (let ai=0;ai<ends.length;ai++) {
    const a=ends[ai],gx=Math.floor(a.x/cellSize),gy=Math.floor(a.y/cellSize);
    for(let cy=gy-1;cy<=gy+1;cy++)for(let cx=gx-1;cx<=gx+1;cx++)for(const bi of grid.get(`${cx},${cy}`)||[]) {
      if(bi<=ai)continue;
      const b=ends[bi],distance=Math.hypot(b.x-a.x,b.y-a.y);
      if(distance<2 || distance>maxGap+1 || Math.abs(a.width-b.width)>2)continue;
      const ux=(b.x-a.x)/distance,uy=(b.y-a.y)/distance;
      const alignmentA=a.dx*ux+a.dy*uy,alignmentB=-(b.dx*ux+b.dy*uy);
      // A 2–5 px cut across a 2–4 px wide stroke gives an imprecise cap
      // position. Relax its chord direction only; the measured tangents must
      // still face almost opposite directions across the same removal region.
      const alignmentMinimum=distance<=6 ? .68 : .88;
      if(alignmentA<alignmentMinimum || alignmentB<alignmentMinimum || a.dx*b.dx+a.dy*b.dy>-.8)continue;
      if(data) {
        const pa=a.colorIndex*4,pb=b.colorIndex*4;
        if(Math.hypot(data[pa]-data[pb],data[pa+1]-data[pb+1],data[pa+2]-data[pb+2])>95)continue;
      }
      const path=curve(a,b,distance),missing=new Set(),supported=new Set();let escaped=false;
      for(const p of path) {
        if(p.x<0||p.y<0||p.x>=width||p.y>=height){escaped=true;break;}
        const i=p.y*width+p.x;
        if(!mask[i]) {
          missing.add(i);
          if(evidence[i])supported.add(i);
          // One-pixel antialiasing margins are acceptable; larger stretches of
          // blank paper are not evidence that two strokes ever belonged together.
          if(!nearMask(evidence,width,height,p.x,p.y,1)){escaped=true;break;}
        }
      }
      if(escaped || missing.size<1 || supported.size/missing.size<minimumEvidence)continue;
      const score=distance/(alignmentA*alignmentB);
      const candidate={ai,bi,a,b,path,score};
      candidates.push(candidate);byEnd[ai].push(candidate);byEnd[bi].push(candidate);
    }
  }
  for(const choices of byEnd)choices.sort((a,b)=>a.score-b.score);
  candidates.sort((a,b)=>a.score-b.score);
  const used=new Set();
  for(const candidate of candidates) {
    const {ai,bi,a,b,path}=candidate;
    if(used.has(ai)||used.has(bi))continue;
    // Competing, almost equally plausible continuations are left for a person.
    if([ai,bi].some(i=>byEnd[i][0]!==candidate || (byEnd[i][1] && byEnd[i][1].score < candidate.score*1.2)))continue;
    let added=0;
    for(const p of path) {
      const diameter=a.width*(1-p.t)+b.width*p.t,radius=(diameter-1)/2;
      for(let y=Math.max(0,Math.floor(p.y-radius));y<=Math.min(height-1,Math.ceil(p.y+radius));y++)
        for(let x=Math.max(0,Math.floor(p.x-radius));x<=Math.min(width-1,Math.ceil(p.x+radius));x++) {
          const i=y*width+x;
          if(mask[i] || result.inferredMask[i] || (x-p.x)**2+(y-p.y)**2>(radius+.3)**2 || !nearMask(evidence,width,height,x,y,1))continue;
          result.mask[i]=1;result.inferredMask[i]=1;added++;
          if(data) {
            const pa=a.colorIndex*4,pb=b.colorIndex*4,pi=i*4;
            for(let c=0;c<3;c++)result.data[pi+c]=Math.round(data[pa+c]*(1-p.t)+data[pb+c]*p.t);
            result.data[pi+3]=255;
          }
        }
    }
    if(added) {result.inferredPixels+=added;result.bridges++;used.add(ai);used.add(bi);}
  }
  return result;
}
