"""Таксация Лайт — настольная программа (Windows/Linux/macOS, Python 3.8+, только стандартная библиотека).

Запуск из исходников:  python taksaciya_app.py
Сборка .exe:           см. desktop/build_exe.bat или .github/workflows/taksaciya-desktop.yml
"""
from __future__ import annotations

import copy
import datetime
import json
import os
import sys
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

HERE = os.path.dirname(os.path.abspath(__file__))
for cand in (os.path.join(HERE, '..'), HERE, getattr(sys, '_MEIPASS', '')):
    if cand and os.path.isdir(os.path.join(cand, 'core')):
        sys.path.insert(0, os.path.abspath(cand))
        break

from core import engine as E  # noqa: E402
from core import importer, reports, xlsx_io  # noqa: E402

APP = 'Таксация Лайт'
COND = ['хор.', 'уд.', 'плох.', 'ненадл.']
ACTIONS = {'': 'сохранить', 'В': 'вырубить', 'П': 'пересадить'}
ACTIONS_BACK = {v: k for k, v in ACTIONS.items()}
FLOWER_KINDS = ['Композиция из многолетников', 'Композиция из 1-2-летников']
LAWN_KINDS = ['Спортивный', 'Партерный', 'Обыкновенный', 'Луговой', 'Мавританский', 'Специального назначения']
TYPES = E.TREE_TYPES + E.SHRUB_TYPES
REF = E.load_reference()


def empty_tree():
    return {'count': '', 'start': '', 'species': '', 'height': '', 'diam': '', 'cond': '', 'action': '', 'note': '', 'reason': '', 'kRepl': ''}


def empty_area():
    return {'kind': '', 'cond': '', 'area': '', 'transplant': False, 'reason': '', 'note': '', 'kRepl': ''}


def blank_project():
    s = E.default_settings(REF)
    s['date'] = datetime.date.today().isoformat()
    return {'format': 'taksaciya-lite', 'version': 1,
            'meta': {'org': '', 'objectNo': '', 'objectName': '', 'stage': '', 'author': ''},
            'settings': s, 'baseValues': [dict(b) for b in REF['baseValues']], 'customSpecies': [],
            'trees': [empty_tree() for _ in range(10)], 'flowers': [], 'lawns': [], 'grass': [],
            'projected': {'perennial': '', 'annual': '', 'lawns': {}}, 'areaTotals': {'flowers': '', 'lawns': '', 'grass': ''}}


def example_project():
    p = blank_project()
    p['meta'].update({'org': 'Пример', 'objectNo': '...-ГП', 'objectName': 'Пример: данные из файла ТАКСАЦИЯ-АВТО 4.2', 'stage': 'С'})
    p['settings']['date'] = REF['defaults'].get('date') or p['settings']['date']
    raw = [(1, 20, 20, 'Ель обыкновенная'), (2, 10, 20, 'Береза повислая'), (1, 15, 20, 'Ива ломкая'), (1, 20, 20, 'Ель обыкновенная'),
           (2, 10, 20, 'Береза повислая'), (1, 14, 20, 'Ель обыкновенная'), (20, 12, 40, 'Ель обыкновенная'), (1, 17, 30, 'Ель обыкновенная'),
           (15, 23, 35, 'Береза повислая'), (1, 23, 23, 'Ива ломкая'), (1, 50, 12, 'Клен остролистный'), (1, 23, 23, 'Яблоня домашняя'),
           (1, 23, 12, 'Береза повислая'), (1, 15, 20, 'Ива ломкая'), (1, 15, 20, 'Ива ломкая')]
    p['trees'] = []
    for c, h, d, sp in raw:
        r = empty_tree()
        r.update({'count': str(c), 'height': str(h), 'diam': str(d), 'species': sp, 'action': 'В', 'reason': 'СЕТИ'})
        p['trees'].append(r)
    p['trees'] += [empty_tree(), empty_tree()]
    return p


def normalize(p):
    b = blank_project()
    out = {k: p.get(k, v) for k, v in b.items()}
    out['settings'] = dict(b['settings'], **(p.get('settings') or {}))
    out['settings']['compMap'] = dict(b['settings']['compMap'], **((p.get('settings') or {}).get('compMap') or {}))
    out['meta'] = dict(b['meta'], **(p.get('meta') or {}))
    out['projected'] = dict({'perennial': '', 'annual': '', 'lawns': {}}, **(p.get('projected') or {}))
    out['projected']['lawns'] = dict(out['projected'].get('lawns') or {})
    out['areaTotals'] = dict({'flowers': '', 'lawns': '', 'grass': ''}, **(p.get('areaTotals') or {}))
    out['trees'] = [dict(empty_tree(), **r) for r in (out.get('trees') or [])] or [empty_tree() for _ in range(5)]
    for k in ('flowers', 'lawns', 'grass'):
        out[k] = [dict(empty_area(), **r) for r in (out.get(k) or [])]
    return out


def xn(v):
    return reports.xn(v)


# ====================================================================== editable grid
class Grid(ttk.Frame):
    """Таблица с редактированием ячеек по месту (Enter/двойной щелчок/ввод символа)."""

    def __init__(self, master, columns, rows_ref, on_change, calc_columns=(), height=18, row_factory=empty_tree):
        super().__init__(master)
        self.columns = columns            # [{'k','l','w','kind':'text'|'combo'|'check','values'}]
        self.calc_columns = list(calc_columns)  # [('id','label',width)]
        self.rows_ref = rows_ref          # функция → список строк проекта
        self.on_change = on_change
        self.row_factory = row_factory
        ids = ['_n'] + [c['k'] for c in columns] + [c[0] for c in self.calc_columns]
        self.tree = ttk.Treeview(self, columns=ids, show='headings', height=height, selectmode='extended')
        self.tree.heading('_n', text='#')
        self.tree.column('_n', width=44, anchor='e', stretch=False)
        for c in columns:
            self.tree.heading(c['k'], text=c['l'])
            self.tree.column(c['k'], width=c.get('w', 90), anchor='w', stretch=False)
        for cid, lbl, w in self.calc_columns:
            self.tree.heading(cid, text=lbl)
            self.tree.column(cid, width=w, anchor='w', stretch=False)
        vs = ttk.Scrollbar(self, orient='vertical', command=self.tree.yview)
        hs = ttk.Scrollbar(self, orient='horizontal', command=self.tree.xview)
        self.tree.configure(yscrollcommand=vs.set, xscrollcommand=hs.set)
        self.tree.grid(row=0, column=0, sticky='nsew')
        vs.grid(row=0, column=1, sticky='ns')
        hs.grid(row=1, column=0, sticky='ew')
        self.rowconfigure(0, weight=1)
        self.columnconfigure(0, weight=1)
        self.tree.tag_configure('err', background='#fde2df')
        self.tree.tag_configure('warn', background='#fff3dc')
        self.tree.tag_configure('rem', foreground='#9b1c13')
        self.tree.tag_configure('tr', foreground='#2c6a4c')
        self.editor = None
        self.cur_col = 1
        self.calc = {}
        t = self.tree
        t.bind('<Double-1>', self._dbl)
        t.bind('<Button-1>', self._click, add='+')
        t.bind('<Return>', lambda e: self.edit_focused())
        t.bind('<F2>', lambda e: self.edit_focused())
        t.bind('<Key>', self._key)
        t.bind('<Delete>', lambda e: self.clear_cells())
        t.bind('<Control-v>', lambda e: self.paste())
        t.bind('<Control-V>', lambda e: self.paste())
        t.bind('<Control-c>', lambda e: self.copy())
        t.bind('<Left>', lambda e: self._move_col(-1))
        t.bind('<Right>', lambda e: self._move_col(1))
        t.bind('<Button-3>', self._menu)
        self.menu = tk.Menu(self, tearoff=0)
        self.menu.add_command(label='Вставить строку ниже', command=self.insert_row)
        self.menu.add_command(label='Копировать строку', command=self.dup_row)
        self.menu.add_command(label='Удалить выделенные строки', command=self.delete_rows)
        self.menu.add_separator()
        self.menu.add_command(label='Добавить 10 строк', command=lambda: self.add_rows(10))
        self.menu.add_command(label='Вставить из буфера (Ctrl+V)', command=self.paste)

    # ---------------- data
    def rows(self):
        return self.rows_ref()

    def refresh(self):
        self.close_editor(commit=True)
        sel = self.tree.selection()
        focus = self.tree.focus()
        self.tree.delete(*self.tree.get_children())
        for i, r in enumerate(self.rows()):
            self.tree.insert('', 'end', iid=str(i), values=self._values(i, r))
        if focus and self.tree.exists(focus):
            self.tree.focus(focus)
            self.tree.selection_set([s for s in sel if self.tree.exists(s)])
        self.update_calc(self.calc)

    def _values(self, i, r):
        vals = [i + 1]
        for c in self.columns:
            v = r.get(c['k'], '')
            if c.get('kind') == 'check':
                v = '✓' if v else ''
            elif c['k'] == 'action':
                v = ACTIONS.get(str(v).upper(), v)
            vals.append(v)
        vals += [self.calc.get(i, {}).get(cid, '') for cid, _, _ in self.calc_columns]
        return vals

    def update_calc(self, calc):
        """calc: {индекс строки: {id: текст, '_tags': (...)}}"""
        self.calc = calc
        for i, r in enumerate(self.rows()):
            iid = str(i)
            if not self.tree.exists(iid):
                continue
            c = calc.get(i, {})
            for cid, _, _ in self.calc_columns:
                self.tree.set(iid, cid, c.get(cid, ''))
            self.tree.item(iid, tags=c.get('_tags', ()))

    # ---------------- editing
    def _col_at(self, x):
        col = self.tree.identify_column(x)
        try:
            return int(col.replace('#', '')) - 1
        except ValueError:
            return None

    def _click(self, ev):
        c = self._col_at(ev.x)
        if c is not None and 1 <= c <= len(self.columns):
            self.cur_col = c

    def _dbl(self, ev):
        row = self.tree.identify_row(ev.y)
        c = self._col_at(ev.x)
        if row and c is not None and 1 <= c <= len(self.columns):
            self.cur_col = c
            self.start_edit(row, c)

    def _key(self, ev):
        if ev.char and ev.char.isprintable() and not (ev.state & 0x4):
            iid = self.tree.focus()
            if iid:
                self.start_edit(iid, self.cur_col, initial=ev.char)
                return 'break'

    def _move_col(self, d):
        self.cur_col = max(1, min(len(self.columns), self.cur_col + d))
        self.tree.heading(self.columns[self.cur_col - 1]['k'])
        return 'break'

    def edit_focused(self):
        iid = self.tree.focus()
        if iid:
            self.start_edit(iid, self.cur_col)
        return 'break'

    def start_edit(self, iid, col, initial=None):
        self.close_editor(commit=True)
        cdef = self.columns[col - 1]
        self.tree.see(iid)
        self.tree.update_idletasks()
        bbox = self.tree.bbox(iid, cdef['k'])
        if not bbox:
            return
        x, y, w, h = bbox
        i = int(iid)
        row = self.rows()[i]
        if cdef.get('kind') == 'check':
            row[cdef['k']] = not row.get(cdef['k'])
            self.tree.set(iid, cdef['k'], '✓' if row[cdef['k']] else '')
            self.on_change()
            return
        if cdef.get('kind') == 'combo':
            vals = cdef['values']() if callable(cdef['values']) else cdef['values']
            ed = ttk.Combobox(self.tree, values=vals)
        else:
            ed = ttk.Entry(self.tree)
        cur = row.get(cdef['k'], '')
        if cdef['k'] == 'action':
            cur = ACTIONS.get(str(cur).upper(), cur)
        ed.insert(0, '' if cur is None else str(cur))
        if initial is not None:
            ed.delete(0, 'end')
            ed.insert(0, initial)
        else:
            ed.select_range(0, 'end')
        ed.place(x=x, y=y, width=max(w, cdef.get('ew', w)), height=h)
        ed.focus_set()
        self.editor = (ed, iid, col)
        ed.bind('<Return>', lambda e: self._commit_move(1))
        ed.bind('<KP_Enter>', lambda e: self._commit_move(1))
        ed.bind('<Tab>', lambda e: self._commit_move(0, 1))
        ed.bind('<Shift-Tab>', lambda e: self._commit_move(0, -1))
        ed.bind('<Escape>', lambda e: self.close_editor(commit=False))
        ed.bind('<FocusOut>', lambda e: self.after(1, lambda: self.close_editor(commit=True)) if self.editor and self.editor[0] is ed else None)
        if isinstance(ed, ttk.Combobox):
            ed.bind('<<ComboboxSelected>>', lambda e: None)

    def close_editor(self, commit=True):
        if not self.editor:
            return
        ed, iid, col = self.editor
        self.editor = None
        try:
            val = ed.get()
        except tk.TclError:
            return
        ed.destroy()
        if commit:
            cdef = self.columns[col - 1]
            i = int(iid)
            if i < len(self.rows()):
                if cdef['k'] == 'action':
                    v = val.strip().lower()
                    val = 'В' if v in ('в', 'вырубить') else 'П' if v in ('п', 'пересадить') else ''
                self.rows()[i][cdef['k']] = val
                self.tree.set(iid, cdef['k'], ACTIONS.get(val, val) if cdef['k'] == 'action' else val)
                self.on_change()
        self.tree.focus_set()

    def _commit_move(self, drow, dcol=0):
        if not self.editor:
            return 'break'
        ed, iid, col = self.editor
        self.close_editor(commit=True)
        i = int(iid) + drow
        if i >= len(self.rows()):
            self.rows().append(self.row_factory())
            self.tree.insert('', 'end', iid=str(i), values=self._values(i, self.rows()[i]))
        col = max(1, min(len(self.columns), col + dcol))
        self.cur_col = col
        self.tree.focus(str(i))
        self.tree.selection_set(str(i))
        if dcol:
            self.start_edit(str(i), col)
        return 'break'

    # ---------------- rows
    def _sel_index(self):
        f = self.tree.focus()
        return int(f) if f else len(self.rows()) - 1

    def insert_row(self):
        i = self._sel_index()
        self.rows().insert(i + 1, self.row_factory())
        self.refresh()
        self.on_change()

    def dup_row(self):
        i = self._sel_index()
        if 0 <= i < len(self.rows()):
            self.rows().insert(i + 1, copy.deepcopy(self.rows()[i]))
            self.refresh()
            self.on_change()

    def delete_rows(self):
        sel = sorted((int(s) for s in self.tree.selection()), reverse=True)
        if not sel:
            return
        if len(sel) > 1 and not messagebox.askyesno(APP, 'Удалить выделенные строки (%d)?' % len(sel)):
            return
        for i in sel:
            del self.rows()[i]
        if not self.rows():
            self.rows().append(self.row_factory())
        self.refresh()
        self.on_change()

    def add_rows(self, n):
        for _ in range(n):
            self.rows().append(self.row_factory())
        self.refresh()
        self.on_change()

    def clear_cells(self):
        for s in self.tree.selection():
            cdef = self.columns[self.cur_col - 1]
            self.rows()[int(s)][cdef['k']] = False if cdef.get('kind') == 'check' else ''
        self.refresh()
        self.on_change()

    def paste(self):
        try:
            text = self.clipboard_get()
        except tk.TclError:
            return 'break'
        lines = text.replace('\r', '').rstrip('\n').split('\n')
        r0 = self._sel_index() if self.tree.focus() else 0
        for li, line in enumerate(lines):
            r = r0 + li
            while r >= len(self.rows()):
                self.rows().append(self.row_factory())
            for vi, val in enumerate(line.split('\t')):
                ci = self.cur_col - 1 + vi
                if ci < len(self.columns):
                    k = self.columns[ci]['k']
                    if self.columns[ci].get('kind') == 'check':
                        self.rows()[r][k] = val.strip().lower() in ('1', 'да', 'п', 'true', '✓')
                    elif k == 'action':
                        v = val.strip().lower()
                        self.rows()[r][k] = 'В' if v in ('в', 'вырубить') else 'П' if v in ('п', 'пересадить') else ''
                    else:
                        self.rows()[r][k] = val.strip()
        self.refresh()
        self.on_change()
        return 'break'

    def copy(self):
        out = []
        for s in self.tree.selection():
            out.append('\t'.join(str(v) for v in self.tree.item(s, 'values')[1:]))
        self.clipboard_clear()
        self.clipboard_append('\n'.join(out))
        return 'break'

    def _menu(self, ev):
        row = self.tree.identify_row(ev.y)
        if row:
            if row not in self.tree.selection():
                self.tree.selection_set(row)
            self.tree.focus(row)
        self.menu.tk_popup(ev.x_root, ev.y_root)


# ====================================================================== application
class App(tk.Tk):
    def __init__(self, path=None):
        super().__init__()
        self.title(APP)
        self.geometry('1360x820')
        self.minsize(900, 560)
        self._style()
        self.project = example_project()
        self.path = None
        self.dirty = False
        self.res = None
        self._recalc_job = None
        self._build_menu()
        self._build_top()
        self.nb = ttk.Notebook(self)
        self.nb.pack(fill='both', expand=True, padx=8, pady=(0, 8))
        self.tab_object = ttk.Frame(self.nb, padding=10)
        self.tab_trees = ttk.Frame(self.nb, padding=6)
        self.tab_areas = ttk.Frame(self.nb, padding=6)
        self.tab_reports = ttk.Frame(self.nb, padding=6)
        self.tab_lib = ttk.Frame(self.nb, padding=6)
        self.nb.add(self.tab_object, text='Объект и настройки')
        self.nb.add(self.tab_trees, text='Деревья и кустарники')
        self.nb.add(self.tab_areas, text='Цветники, газоны')
        self.nb.add(self.tab_reports, text='Ведомости')
        self.nb.add(self.tab_lib, text='Справочники')
        self._build_trees()
        self._build_areas()
        self._build_reports()
        self._build_lib()
        self._build_object()
        self.nb.select(self.tab_trees)
        self.nb.bind('<<NotebookTabChanged>>', lambda e: self._tab_changed())
        self.protocol('WM_DELETE_WINDOW', self.on_close)
        if path:
            self.open_path(path)
        else:
            self.recalc()

    # ------------------------------------------------------------ look
    def _style(self):
        st = ttk.Style(self)
        if 'vista' in st.theme_names():
            st.theme_use('vista')
        elif 'clam' in st.theme_names():
            st.theme_use('clam')
        base = ('Segoe UI', 10) if sys.platform == 'win32' else ('DejaVu Sans', 10)
        self.option_add('*Font', base)
        st.configure('Treeview', rowheight=24, font=base)
        st.configure('Treeview.Heading', font=(base[0], base[1], 'bold'))
        st.configure('Sum.TLabel', font=(base[0], 11, 'bold'), foreground='#2c6a4c')
        st.configure('Muted.TLabel', foreground='#5b6a61')
        st.configure('H.TLabel', font=(base[0], 12, 'bold'))
        self.base_font = base

    def _build_menu(self):
        m = tk.Menu(self)
        f = tk.Menu(m, tearoff=0)
        f.add_command(label='Новый проект', command=self.new_project, accelerator='Ctrl+N')
        f.add_command(label='Открыть…', command=self.open_dialog, accelerator='Ctrl+O')
        f.add_command(label='Импорт из ТАКСАЦИЯ-АВТО (.xlsm)…', command=self.import_dialog)
        f.add_separator()
        f.add_command(label='Сохранить', command=self.save, accelerator='Ctrl+S')
        f.add_command(label='Сохранить как…', command=self.save_as)
        f.add_command(label='Ведомости в Excel…', command=self.export_xlsx, accelerator='Ctrl+E')
        f.add_separator()
        f.add_command(label='Открыть пример', command=self.load_example)
        f.add_separator()
        f.add_command(label='Выход', command=self.on_close)
        m.add_cascade(label='Файл', menu=f)
        h = tk.Menu(m, tearoff=0)
        h.add_command(label='Как вносить данные', command=self.show_help)
        h.add_command(label='О программе', command=lambda: messagebox.showinfo(APP, ABOUT))
        m.add_cascade(label='Справка', menu=h)
        self.config(menu=m)
        self.bind_all('<Control-n>', lambda e: self.new_project())
        self.bind_all('<Control-o>', lambda e: self.open_dialog())
        self.bind_all('<Control-s>', lambda e: self.save())
        self.bind_all('<Control-e>', lambda e: self.export_xlsx())

    def _build_top(self):
        top = ttk.Frame(self, padding=(10, 8))
        top.pack(fill='x')
        self.lbl_obj = ttk.Label(top, text='', style='H.TLabel')
        self.lbl_obj.pack(side='left')
        self.lbl_sum = ttk.Label(top, text='', style='Sum.TLabel')
        self.lbl_sum.pack(side='left', padx=20)
        self.btn_err = ttk.Button(top, text='Ошибок нет', command=self.show_errors)
        self.btn_err.pack(side='right')
        ttk.Button(top, text='В Excel…', command=self.export_xlsx).pack(side='right', padx=6)

    # ------------------------------------------------------------ object/settings tab
    def _build_object(self):
        for w in self.tab_object.winfo_children():
            w.destroy()
        p = self.project
        S = p['settings']
        canvas = tk.Canvas(self.tab_object, highlightthickness=0)
        sb = ttk.Scrollbar(self.tab_object, orient='vertical', command=canvas.yview)
        inner = ttk.Frame(canvas)
        inner.bind('<Configure>', lambda e: canvas.configure(scrollregion=canvas.bbox('all')))
        canvas.create_window((0, 0), window=inner, anchor='nw')
        canvas.configure(yscrollcommand=sb.set)
        canvas.pack(side='left', fill='both', expand=True)
        sb.pack(side='right', fill='y')

        def section(title, row, col):
            fr = ttk.LabelFrame(inner, text=title, padding=10)
            fr.grid(row=row, column=col, sticky='nsew', padx=6, pady=6)
            return fr

        def entry(fr, r, label, get, setv, width=34):
            ttk.Label(fr, text=label).grid(row=r, column=0, sticky='w', pady=2)
            var = tk.StringVar(value='' if get() is None else str(get()))
            e = ttk.Entry(fr, textvariable=var, width=width)
            e.grid(row=r, column=1, sticky='we', pady=2, padx=(8, 0))
            var.trace_add('write', lambda *a: (setv(var.get()), self.changed()))
            return e

        def combo(fr, r, label, values, get, setv, width=24):
            ttk.Label(fr, text=label).grid(row=r, column=0, sticky='w', pady=2)
            var = tk.StringVar(value=get())
            c = ttk.Combobox(fr, textvariable=var, values=values, state='readonly', width=width)
            c.grid(row=r, column=1, sticky='w', pady=2, padx=(8, 0))
            var.trace_add('write', lambda *a: (setv(var.get()), self.changed()))
            return c

        def check(fr, r, label, key, sub=''):
            var = tk.BooleanVar(value=bool(S.get(key)))
            cb = ttk.Checkbutton(fr, text=label, variable=var)
            cb.grid(row=r, column=0, columnspan=2, sticky='w', pady=2)
            if sub:
                ttk.Label(fr, text=sub, style='Muted.TLabel').grid(row=r, column=2, sticky='w', padx=8)
            var.trace_add('write', lambda *a: (S.__setitem__(key, var.get()), self.changed()))

        m = p['meta']
        fr = section('Объект', 0, 0)
        for i, (k, l) in enumerate([('org', 'Организация'), ('objectNo', 'Номер объекта'), ('objectName', 'Наименование объекта'),
                                    ('stage', 'Стадия проекта'), ('author', 'Автор (ФИО)')]):
            entry(fr, i, l, lambda k=k: m.get(k, ''), lambda v, k=k: m.__setitem__(k, v))
        fr = section('Базовая величина', 0, 1)
        entry(fr, 0, 'Дата расчёта (ГГГГ-ММ-ДД)', lambda: S['date'], lambda v: S.__setitem__('date', v.strip()), 14)
        ttk.Label(fr, text='По календарю:').grid(row=1, column=0, sticky='w')
        self.lbl_bv = ttk.Label(fr, text='', style='Muted.TLabel')
        self.lbl_bv.grid(row=1, column=1, sticky='w', padx=(8, 0))
        entry(fr, 2, 'Своё значение, руб. (пусто — по календарю)', lambda: S.get('baseValue') or '',
              lambda v: S.__setitem__('baseValue', None if v.strip() == '' else v.strip().replace(',', '.')), 10)
        ttk.Label(fr, text='Календарь базовых величин — на вкладке «Справочники».', style='Muted.TLabel').grid(row=3, column=0, columnspan=2, sticky='w', pady=(6, 0))
        fr = section('Коэффициенты для всего объекта (подписи — как в оригинале)', 1, 0)
        check(fr, 0, 'К1 — «зона особ. охр.?»', 'k1', 'да → К1 = 2')
        check(fr, 1, 'К2 — «бюджет или нет?»', 'k2', 'да → К2 = 0,5')
        check(fr, 2, 'К3 — «помеха инж.?»', 'k3', 'да → К3 = 0,5')
        check(fr, 3, 'К4 — «вне насел. пункта?»', 'k4', 'да → К4 = 0,1')
        fr = section('Как считать', 1, 1)
        combo(fr, 0, 'Компенсация', ['выплатами', 'посадками'], lambda: 'выплатами' if S['money'] else 'посадками',
              lambda v: S.__setitem__('money', v == 'выплатами'))
        combo(fr, 1, 'Единицы выплат', E.UNITS, lambda: S['unit'], lambda v: S.__setitem__('unit', v))
        combo(fr, 2, 'Графа «компенсация»', ['формула + итог', 'итог'], lambda: S['view'], lambda v: S.__setitem__('view', v))
        combo(fr, 3, 'Состояние по умолчанию', COND, lambda: S['defaultCond'], lambda v: S.__setitem__('defaultCond', v))
        entry(fr, 4, 'Масса куста при вырубке, кг', lambda: S['shrubMassKg'],
              lambda v: S.__setitem__('shrubMassKg', float(v.replace(',', '.')) if v.strip() else 0), 8)
        combo(fr, 5, 'Учёт проектируемых цветников/газонов', ['метрами', 'стоимостью'], lambda: S['flowerMode'], lambda v: S.__setitem__('flowerMode', v))
        check(fr, 6, 'Показывать поросль в балансе', 'showPlots')
        check(fr, 7, 'Живую изгородь также штуками', 'hedgePieces')
        fr = section('Причины вырубки', 2, 0)
        fr.columnconfigure(0, weight=1)
        self.reason_grid = Grid(fr, [{'k': 'name', 'l': 'Причина вырубки', 'w': 220}, {'k': 'group', 'l': 'Группа работ', 'w': 160},
                                     {'k': 'replace', 'l': 'Заменяемые коэффициенты', 'w': 180, 'kind': 'combo',
                                      'values': ['', '1', '2', '3', '4', '12', '24', '1234', E.NO_COMP]}],
                                lambda: self.project['settings']['reasons'], self.changed, height=6,
                                row_factory=lambda: {'name': '', 'group': '', 'replace': ''})
        self.reason_grid.grid(row=0, column=0, sticky='nsew')
        self.reason_grid.refresh()
        ttk.Label(fr, text='«12» — для причины К1 и К2 меняются на обратные; «без компенcации» — коэффициенты 0. '
                           'Правый щелчок — добавить/удалить.', style='Muted.TLabel', wraplength=560).grid(row=1, column=0, sticky='w', pady=(4, 0))
        fr = section('Компенсировать типами (для посадок)', 2, 1)
        cm = S['compMap']
        for i, t in enumerate(TYPES):
            combo(fr, i, t, [''] + TYPES, lambda t=t: cm.get(t, ''), lambda v, t=t: cm.__setitem__(t, v), 10)
        combo(fr, len(TYPES), 'поросль деревьев →', ['хвой.', 'медл.', 'быст.'], lambda: cm.get('plotTree', ''), lambda v: cm.__setitem__('plotTree', v), 10)
        combo(fr, len(TYPES) + 1, 'поросль кустарников →', E.SHRUB_TYPES, lambda: cm.get('plotShrub', ''), lambda v: cm.__setitem__('plotShrub', v), 10)
        self._update_bv_label()

    def _update_bv_label(self):
        if not self.res or not hasattr(self, 'lbl_bv'):
            return
        r = self.res['baseValueRow']
        try:
            self.lbl_bv.configure(text=('%s руб. с %s %s' % (xn(r['rub']), r['from'], ('— ' + r['act']) if r.get('act') else '')) if r else 'нет значения на дату')
        except tk.TclError:
            pass

    # ------------------------------------------------------------ trees tab
    def species_values(self):
        names = [s['name'] for s in REF['special']] + [s['name'] for s in REF['species']]
        names += [s['name'] for s in self.project.get('customSpecies') or [] if s.get('name')]
        seen, out = set(), []
        for n in names:
            if n.lower() not in seen:
                seen.add(n.lower())
                out.append(n)
        return out

    def _build_trees(self):
        bar = ttk.Frame(self.tab_trees)
        bar.pack(fill='x', pady=(0, 6))
        ttk.Button(bar, text='+ строка', command=lambda: self.tree_grid.insert_row()).pack(side='left')
        ttk.Button(bar, text='+ 10 строк', command=lambda: self.tree_grid.add_rows(10)).pack(side='left', padx=4)
        ttk.Button(bar, text='Удалить пустые', command=self.remove_empty).pack(side='left')
        ttk.Label(bar, text='Двойной щелчок или Enter — правка ячейки; Tab — соседняя ячейка; Ctrl+V — вставка из Excel; '
                            'правый щелчок — строки. Диаметр: «20,15» — стволы, «-» — куст, «0-4» — поросль.',
                  style='Muted.TLabel').pack(side='left', padx=10)
        cols = [
            {'k': 'count', 'l': 'Кол-во', 'w': 60}, {'k': 'start', 'l': '№ нач.', 'w': 60},
            {'k': 'species', 'l': 'Порода, вид', 'w': 210, 'kind': 'combo', 'values': self.species_values, 'ew': 260},
            {'k': 'height', 'l': 'Высота, м', 'w': 70}, {'k': 'diam', 'l': 'Диаметр, см', 'w': 100},
            {'k': 'cond', 'l': 'Состояние', 'w': 80, 'kind': 'combo', 'values': [''] + COND},
            {'k': 'action', 'l': 'Действие', 'w': 90, 'kind': 'combo', 'values': ['сохранить', 'вырубить', 'пересадить']},
            {'k': 'reason', 'l': 'Причина', 'w': 120, 'kind': 'combo', 'values': lambda: [r['name'] for r in self.project['settings']['reasons'] if r.get('name')]},
            {'k': 'kRepl', 'l': 'Инд. К', 'w': 60, 'kind': 'combo', 'values': ['', '1', '2', '3', '4', '12', '24', '1234', E.NO_COMP]},
            {'k': 'note', 'l': 'Примечание', 'w': 110},
        ]
        calc = [('no', '№ по плану', 80), ('ty', 'Тип', 60), ('k', 'К1;К2;К3;К4', 110), ('g', 'Компенсация', 330), ('y', 'Итог', 70)]
        self.tree_grid = Grid(self.tab_trees, cols, lambda: self.project['trees'], self.changed, calc_columns=calc, height=24)
        self.tree_grid.pack(fill='both', expand=True)

    def remove_empty(self):
        self.project['trees'] = [r for r in self.project['trees'] if str(r.get('species') or '').strip()] or [empty_tree() for _ in range(5)]
        self.tree_grid.refresh()
        self.changed()

    # ------------------------------------------------------------ areas tab
    def _build_areas(self):
        self.area_grids = {}
        self.area_labels = {}
        pw = ttk.Frame(self.tab_areas)
        pw.pack(fill='both', expand=True)
        specs = [('flowers', 'Цветники', FLOWER_KINDS), ('lawns', 'Газоны', LAWN_KINDS), ('grass', 'Иной травяной покров', None)]
        for i, (key, title, kinds) in enumerate(specs):
            fr = ttk.LabelFrame(pw, text=title, padding=6)
            fr.grid(row=i, column=0, sticky='nsew', pady=3)
            pw.rowconfigure(i, weight=1)
            cols = []
            if kinds:
                cols.append({'k': 'kind', 'l': 'Вид', 'w': 210, 'kind': 'combo', 'values': kinds})
            cols += [{'k': 'cond', 'l': 'Состояние', 'w': 80, 'kind': 'combo', 'values': [''] + COND},
                     {'k': 'area', 'l': 'Площадь, м²', 'w': 90},
                     {'k': 'transplant', 'l': 'Пересадить', 'w': 80, 'kind': 'check'},
                     {'k': 'reason', 'l': 'Причина', 'w': 120, 'kind': 'combo', 'values': lambda: [r['name'] for r in self.project['settings']['reasons'] if r.get('name')]},
                     {'k': 'note', 'l': 'Примечание', 'w': 110},
                     {'k': 'kRepl', 'l': 'Инд. К', 'w': 60, 'kind': 'combo', 'values': ['', '1', '2', '3', '4', '12', '24', E.NO_COMP]}]
            g = Grid(fr, cols, lambda key=key: self.project[key], self.changed,
                     calc_columns=[('k', 'К1;К2;К3;К4', 110), ('e', 'Компенсационные выплаты', 420)], height=4, row_factory=empty_area)
            g.pack(fill='both', expand=True)
            self.area_grids[key] = g
            bar = ttk.Frame(fr)
            bar.pack(fill='x', pady=(4, 0))
            ttk.Button(bar, text='+ строка', command=lambda g=g: g.add_rows(1)).pack(side='left')
            ttk.Label(bar, text='Всего существующих на участке, м²:').pack(side='left', padx=(12, 4))
            var = tk.StringVar(value=str(self.project['areaTotals'].get(key, '')))
            ttk.Entry(bar, textvariable=var, width=10).pack(side='left')
            var.trace_add('write', lambda *a, key=key, var=var: (self.project['areaTotals'].__setitem__(key, var.get()), self.changed()))
            lbl = ttk.Label(bar, text='', style='Muted.TLabel')
            lbl.pack(side='left', padx=12)
            self.area_labels[key] = (lbl, var)
        pr = ttk.LabelFrame(pw, text='Проектируемое озеленение, м²', padding=6)
        pr.grid(row=0, column=1, rowspan=3, sticky='nsew', padx=(8, 0))
        pw.columnconfigure(0, weight=1)
        self.proj_vars = []
        fields = [('perennial', 'Цветники из многолетников'), ('annual', 'Цветники из 1-2-летников')] + [('lawn:' + k, 'Газон «%s»' % k) for k in LAWN_KINDS]
        for i, (k, l) in enumerate(fields):
            ttk.Label(pr, text=l).grid(row=i, column=0, sticky='w', pady=2)
            var = tk.StringVar()
            ttk.Entry(pr, textvariable=var, width=10).grid(row=i, column=1, padx=6)
            var.trace_add('write', lambda *a, k=k, var=var: self._set_proj(k, var.get()))
            self.proj_vars.append((k, var))
        ttk.Label(pr, text='Режим зачёта — в настройках.\nОстаток газона засчитывается\nза иной травяной покров.', style='Muted.TLabel').grid(row=len(fields), column=0, columnspan=2, sticky='w', pady=(8, 0))

    def _set_proj(self, k, v):
        if getattr(self, '_loading', False):
            return
        if k.startswith('lawn:'):
            self.project['projected'].setdefault('lawns', {})[k[5:]] = v
        else:
            self.project['projected'][k] = v
        self.changed()

    def _load_area_fields(self):
        self._loading = True
        try:
            for k, var in self.proj_vars:
                v = self.project['projected'].get('lawns', {}).get(k[5:]) if k.startswith('lawn:') else self.project['projected'].get(k)
                var.set('' if v is None else str(v))
            for key, (lbl, var) in self.area_labels.items():
                var.set(str(self.project['areaTotals'].get(key, '') or ''))
        finally:
            self._loading = False

    # ------------------------------------------------------------ reports tab
    def _build_reports(self):
        bar = ttk.Frame(self.tab_reports)
        bar.pack(fill='x', pady=(0, 6))
        ttk.Label(bar, text='Ведомость:').pack(side='left')
        self.rep_var = tk.StringVar(value=reports.REPORTS[1][0])
        cb = ttk.Combobox(bar, textvariable=self.rep_var, values=[n for n, _ in reports.REPORTS], state='readonly', width=26)
        cb.pack(side='left', padx=6)
        cb.bind('<<ComboboxSelected>>', lambda e: self.show_report())
        ttk.Button(bar, text='Все ведомости в Excel…', command=self.export_xlsx).pack(side='left', padx=6)
        ttk.Button(bar, text='Копировать таблицу', command=self.copy_report).pack(side='left')
        self.rep_title = ttk.Label(self.tab_reports, text='', style='H.TLabel')
        self.rep_title.pack(anchor='w')
        self.rep_sub = ttk.Label(self.tab_reports, text='', style='Muted.TLabel')
        self.rep_sub.pack(anchor='w', pady=(0, 4))
        fr = ttk.Frame(self.tab_reports)
        fr.pack(fill='both', expand=True)
        self.rep_tree = ttk.Treeview(fr, show='headings')
        vs = ttk.Scrollbar(fr, orient='vertical', command=self.rep_tree.yview)
        hs = ttk.Scrollbar(fr, orient='horizontal', command=self.rep_tree.xview)
        self.rep_tree.configure(yscrollcommand=vs.set, xscrollcommand=hs.set)
        self.rep_tree.grid(row=0, column=0, sticky='nsew')
        vs.grid(row=0, column=1, sticky='ns')
        hs.grid(row=1, column=0, sticky='ew')
        fr.rowconfigure(0, weight=1)
        fr.columnconfigure(0, weight=1)
        self.rep_tree.tag_configure('cap', background='#e2efe7', font=(self.base_font[0], self.base_font[1], 'bold'))
        self.rep_tree.tag_configure('head', background='#eef1ec', font=(self.base_font[0], self.base_font[1], 'bold'))
        self.rep_tree.tag_configure('tot', background='#f6f8f4')
        self.rep_notes = ttk.Label(self.tab_reports, text='', style='Muted.TLabel', justify='left')
        self.rep_notes.pack(anchor='w', pady=(4, 0))

    def current_report(self):
        name = self.rep_var.get()
        for n, fn in reports.REPORTS:
            if n == name:
                return fn(self.res, self.project)
        return reports.rep_removal(self.res, self.project)

    def show_report(self):
        if not self.res:
            return
        rep = self.current_report()
        ncol = max([len(p['head']) for p in rep['parts']] + [1])
        ids = ['c%d' % i for i in range(ncol)]
        t = self.rep_tree
        t.delete(*t.get_children())
        t.configure(columns=ids)
        widths = rep.get('widths') or [16] * ncol
        multi = len(rep['parts']) > 1
        first_head = rep['parts'][0]['head'] if rep['parts'] else []
        for i, cid in enumerate(ids):
            t.heading(cid, text='' if multi else (first_head[i] if i < len(first_head) else ''))
            t.column(cid, width=int((widths[i] if i < len(widths) else 14) * 8), anchor='w', stretch=False)
        for p in rep['parts']:
            if multi or p.get('caption'):
                if p.get('caption'):
                    t.insert('', 'end', values=[p['caption']], tags=('cap',))
                t.insert('', 'end', values=p['head'], tags=('head',))
            for r in p['rows']:
                t.insert('', 'end', values=[xn(c) for c in r])
            for r in p.get('totals', []):
                t.insert('', 'end', values=[xn(c) for c in r], tags=('tot',))
        if not rep['parts']:
            t.insert('', 'end', values=[rep.get('empty', 'Нет данных.')])
        self.rep_title.configure(text=rep['title'])
        self.rep_sub.configure(text=rep.get('sub') or '')
        notes = list(rep.get('notes') or [])
        if self.res['errors']:
            notes.append('Внимание: в данных есть ошибки (%d) — такие строки не вошли в итоги.' % len(self.res['errors']))
        self.rep_notes.configure(text='\n'.join(notes))

    def copy_report(self):
        rep = self.current_report()
        lines = [rep['title']]
        for p in rep['parts']:
            if p.get('caption'):
                lines += ['', p['caption']]
            lines.append('\t'.join(p['head']))
            for r in p['rows'] + p.get('totals', []):
                lines.append('\t'.join(xn(c) for c in r))
        self.clipboard_clear()
        self.clipboard_append('\n'.join(lines))
        self.status('Таблица скопирована — вставьте в Excel или Word.')

    # ------------------------------------------------------------ library tab
    def _build_lib(self):
        top = ttk.Frame(self.tab_lib)
        top.pack(fill='both', expand=True)
        left = ttk.LabelFrame(top, text='Календарь базовых величин', padding=6)
        left.grid(row=0, column=0, sticky='nsew', padx=(0, 6))
        self.bv_grid = Grid(left, [{'k': 'from', 'l': 'Действует с (ГГГГ-ММ-ДД)', 'w': 170}, {'k': 'rub', 'l': 'Рублей', 'w': 70},
                                   {'k': 'act', 'l': 'Постановление', 'w': 300}],
                            lambda: self.project['baseValues'], self._bv_changed, height=10, row_factory=lambda: {'from': '', 'rub': '', 'act': ''})
        self.bv_grid.pack(fill='both', expand=True)
        b = ttk.Frame(left)
        b.pack(fill='x', pady=(4, 0))
        ttk.Button(b, text='Добавить значение', command=lambda: self.bv_grid.add_rows(1)).pack(side='left')
        ttk.Button(b, text='Вернуть календарь из программы', command=self.reset_bv).pack(side='left', padx=6)
        right = ttk.LabelFrame(top, text='Свои породы (тип: ' + ', '.join(TYPES) + '; твёрдость: мяг., хвн., твд.)', padding=6)
        right.grid(row=0, column=1, sticky='nsew')
        self.cs_grid = Grid(right, [{'k': 'name', 'l': 'Название', 'w': 200}, {'k': 'shrub', 'l': 'Куст', 'w': 50, 'kind': 'check'},
                                    {'k': 'type', 'l': 'Тип', 'w': 70, 'kind': 'combo', 'values': TYPES},
                                    {'k': 'hard', 'l': 'Твёрдость', 'w': 80, 'kind': 'combo', 'values': ['', 'мяг.', 'хвн.', 'твд.']},
                                    {'k': 'ballD', 'l': 'Ø кома', 'w': 60}, {'k': 'ballH', 'l': 'h кома', 'w': 60},
                                    {'k': 'invasive', 'l': 'Инвазивный', 'w': 80, 'kind': 'check'}],
                            lambda: self.project.setdefault('customSpecies', []), self._cs_changed, height=10,
                            row_factory=lambda: {'name': '', 'alt': '', 'shrub': False, 'type': 'быст.', 'hard': 'твд.', 'ballD': None, 'ballH': None,
                                                 'invasive': False, 'inList': False, 'own': None})
        self.cs_grid.pack(fill='both', expand=True)
        ttk.Button(right, text='Добавить породу', command=lambda: self.cs_grid.add_rows(1)).pack(anchor='w', pady=(4, 0))
        lib = ttk.LabelFrame(top, text='Библиотека пород (%d) — из файла ТАКСАЦИЯ-АВТО 4.2' % len(REF['species']), padding=6)
        lib.grid(row=1, column=0, columnspan=2, sticky='nsew', pady=(6, 0))
        top.rowconfigure(1, weight=1)
        top.columnconfigure(0, weight=1)
        top.columnconfigure(1, weight=1)
        sb = ttk.Frame(lib)
        sb.pack(fill='x')
        ttk.Label(sb, text='Поиск:').pack(side='left')
        self.lib_q = tk.StringVar()
        ttk.Entry(sb, textvariable=self.lib_q, width=30).pack(side='left', padx=6)
        self.lib_q.trace_add('write', lambda *a: self.fill_lib())
        fr = ttk.Frame(lib)
        fr.pack(fill='both', expand=True)
        self.lib_tree = ttk.Treeview(fr, columns=('n', 'a', 't', 'h', 'b', 'i'), show='headings', height=10)
        for cid, l, w in (('n', 'Вид', 230), ('a', 'Второе название, латынь', 260), ('t', 'Тип', 70), ('h', 'Твёрдость', 80),
                          ('b', 'Ø/h кома при h<1 м', 130), ('i', 'Инвазивный', 90)):
            self.lib_tree.heading(cid, text=l)
            self.lib_tree.column(cid, width=w, stretch=False)
        vs = ttk.Scrollbar(fr, orient='vertical', command=self.lib_tree.yview)
        self.lib_tree.configure(yscrollcommand=vs.set)
        self.lib_tree.pack(side='left', fill='both', expand=True)
        vs.pack(side='right', fill='y')
        self.fill_lib()

    def fill_lib(self):
        q = self.lib_q.get().lower()
        t = self.lib_tree
        t.delete(*t.get_children())
        for s in REF['species']:
            if q and q not in (s['name'] + ' ' + s['alt'] + ' ' + s['type']).lower():
                continue
            ball = '' if s['ballD'] is None else '%s / %s' % (xn(s['ballD']), xn(s['ballH']))
            t.insert('', 'end', values=(s['name'], s['alt'], s['type'], s['hard'], ball, 'да' if s['invasive'] else ''))

    def _bv_changed(self):
        for b in self.project['baseValues']:
            if isinstance(b.get('rub'), str):
                try:
                    b['rub'] = float(b['rub'].replace(',', '.')) if b['rub'].strip() else ''
                    if isinstance(b['rub'], float) and b['rub'] == int(b['rub']):
                        b['rub'] = int(b['rub'])
                except ValueError:
                    pass
        self.changed()

    def _cs_changed(self):
        for s in self.project.get('customSpecies') or []:
            for k in ('ballD', 'ballH'):
                if isinstance(s.get(k), str):
                    try:
                        s[k] = float(s[k].replace(',', '.')) if s[k].strip() else None
                    except ValueError:
                        s[k] = None
        self.changed()

    def reset_bv(self):
        self.project['baseValues'] = [dict(b) for b in REF['baseValues']]
        self.bv_grid.refresh()
        self.changed()

    # ------------------------------------------------------------ calc
    def changed(self):
        if getattr(self, '_loading', False):
            return
        self.dirty = True
        self._title()
        if self._recalc_job:
            self.after_cancel(self._recalc_job)
        self._recalc_job = self.after(200, self.recalc)

    def recalc(self):
        self._recalc_job = None
        try:
            self.res = E.compute(self.project, REF)
        except Exception as ex:  # noqa: BLE001 — показать пользователю любую ошибку расчёта
            self.status('Ошибка расчёта: %s' % ex)
            return
        res = self.res
        S = res['settings']
        T = res['removal']
        if S['money']:
            total = E.money_text(T['money']['total'], S, res['baseValue'])
            unit = {'руб.': ' руб.', 'баз.в.': ' б.в.'}.get(S['unit'], ' б.в. (руб.)')
            s = 'Итого выплат: %s%s' % (total, unit)
        else:
            s = 'Саженцев деревьев: %s шт.; кустарников: %s' % (xn(T['plant']['trees']), xn(T['plant']['shrubsAll']))
        s += '   ·   удаляется %s дер. / %s куст.   ·   древесина %s м³   ·   базовая величина %s руб.' % (
            xn(T['count']['trees']), xn(T['count']['shrubsC2035']), xn(E.xround(res['volume']['stemsM3']['all'], 2)), xn(res['baseValue']))
        self.lbl_sum.configure(text=s)
        m = self.project['meta']
        self.lbl_obj.configure(text=' · '.join(str(x) for x in (m.get('objectNo'), m.get('objectName')) if x) or 'Новый объект')
        n = len(res['errors'])
        self.btn_err.configure(text='Ошибок: %d' % n if n else 'Ошибок нет')
        calc = {}
        for r in res['rows']:
            g, y = '', ''
            if r['removed'] and r['rm']:
                g = r['rm']['G']
                y = xn(r['rm']['Y']) if E.is_num(r['rm']['Y']) else ('в итогах' if r['rm']['Y'] == '-' else '')
            elif r['transplanted']:
                g = 'ком: ' + (r.get('ball') or '—')
            else:
                g = r['H']
            tags = ('err',) if r['errors'] else ('warn',) if r['warnings'] else ()
            tags += ('rem',) if r['removed'] else ('tr',) if r['transplanted'] else ()
            calc[r['idx']] = {'no': r['no'], 'ty': r['N'] or '—', 'k': ' '.join(r['coef']['str'].split()) if r['coef'] else '',
                              'g': ('; '.join(r['errors']) + ' | ' if r['errors'] else '') + str(g), 'y': y, '_tags': tags}
        self.tree_grid.update_calc(calc)
        for key, g in self.area_grids.items():
            sec = res[key]
            c = {}
            for r in sec['rows']:
                c[r['idx']] = {'k': ' '.join(r['coef']['str'].split()) if r['coef'] else '',
                               'e': '; '.join(r['errors']) if r['errors'] else r['E'], '_tags': ('err',) if r['errors'] else ()}
            g.update_calc(c)
            self.area_labels[key][0].configure(text='Удаляется %s м²; к выплате: %s' % (xn(sec['area']), reports.area_result_text(sec, res)))
        self._update_bv_label()
        if self.nb.select() == str(self.tab_reports):
            self.show_report()

    def _tab_changed(self):
        if self.nb.select() == str(self.tab_reports):
            self.show_report()

    def status(self, text):
        self.lbl_sum.configure(text=text)

    def show_errors(self):
        if not self.res or not self.res['errors']:
            messagebox.showinfo(APP, 'Ошибок нет.')
            return
        lines = []
        for e in self.res['errors'][:60]:
            where = ('строка %d (№ %s)' % (e['row'] + 1, e.get('no', ''))) if e['section'] == 'trees' else \
                ({'flowers': 'цветники', 'lawns': 'газоны', 'grass': 'трав. покров'}[e['section']] + ', строка %d' % (e['row'] + 1))
            lines.append('%s — %s' % (where, e['msg']))
        if len(self.res['errors']) > 60:
            lines.append('… и ещё %d' % (len(self.res['errors']) - 60))
        messagebox.showwarning(APP, 'Строки с ошибками не входят в итоги:\n\n' + '\n'.join(lines))

    # ------------------------------------------------------------ files
    def _title(self):
        name = os.path.basename(self.path) if self.path else 'без имени'
        self.title('%s — %s%s' % (APP, name, ' *' if self.dirty else ''))

    def load_project(self, p, path=None):
        self._loading = True
        try:
            self.project = normalize(p)
            self.path = path
            self.dirty = False
            self._build_object()
            for g in [self.tree_grid, self.bv_grid, self.cs_grid] + list(self.area_grids.values()):
                g.refresh()
        finally:
            self._loading = False
        self._load_area_fields()
        self._title()
        self.recalc()

    def confirm_discard(self):
        if not self.dirty:
            return True
        ans = messagebox.askyesnocancel(APP, 'Сохранить изменения в текущем проекте?')
        if ans is None:
            return False
        if ans:
            return self.save()
        return True

    def new_project(self):
        if self.confirm_discard():
            self.load_project(blank_project())
            self.nb.select(self.tab_object)

    def load_example(self):
        if self.confirm_discard():
            self.load_project(example_project())

    def open_dialog(self):
        if not self.confirm_discard():
            return
        path = filedialog.askopenfilename(title='Открыть проект', filetypes=[('Проект или файл ТАКСАЦИЯ-АВТО', '*.json *.xlsm *.xlsx'),
                                                                             ('Проект Таксации Лайт', '*.json'), ('ТАКСАЦИЯ-АВТО', '*.xlsm *.xlsx')])
        if path:
            self.open_path(path)

    def import_dialog(self):
        if not self.confirm_discard():
            return
        path = filedialog.askopenfilename(title='Импорт из ТАКСАЦИЯ-АВТО', filetypes=[('Файл ТАКСАЦИЯ-АВТО', '*.xlsm *.xlsx')])
        if path:
            self.open_path(path)

    def open_path(self, path):
        try:
            if path.lower().endswith(('.xlsm', '.xlsx')):
                self.config(cursor='watch')
                self.update()
                r = importer.import_file(path, REF)
                r['project']['trees'] += [empty_tree(), empty_tree()]
                self.load_project(r['project'], None)
                self.dirty = True
                self._title()
                messagebox.showinfo(APP, 'Импортировано строк: %d.\n%s\nСохраните проект в новом формате (Файл → Сохранить).' % (r['count'], ' '.join(r['warnings'])))
            else:
                with open(path, encoding='utf-8') as f:
                    p = json.load(f)
                if 'trees' not in p and 'settings' not in p:
                    raise ValueError('это не файл проекта')
                self.load_project(p, path)
        except Exception as ex:  # noqa: BLE001
            messagebox.showerror(APP, 'Не удалось открыть файл:\n%s' % ex)
        finally:
            self.config(cursor='')

    def file_base(self):
        m = self.project['meta']
        b = ('%s %s' % (m.get('objectNo') or '', m.get('objectName') or 'Таксация'))
        b = ''.join(' ' if ch in '\\/:*?"<>|' else ch for ch in b).strip(' .…')
        return ' '.join(b.split())[:80] or 'Таксация'

    def save(self):
        if not self.path:
            return self.save_as()
        try:
            with open(self.path, 'w', encoding='utf-8') as f:
                json.dump(self.project, f, ensure_ascii=False, indent=1)
            self.dirty = False
            self._title()
            self.status('Сохранено: %s' % self.path)
            return True
        except OSError as ex:
            messagebox.showerror(APP, 'Не удалось сохранить:\n%s' % ex)
            return False

    def save_as(self):
        path = filedialog.asksaveasfilename(title='Сохранить проект', defaultextension='.json', initialfile=self.file_base() + '.taks.json',
                                            filetypes=[('Проект Таксации Лайт', '*.json')])
        if not path:
            return False
        self.path = path
        return self.save()

    def export_xlsx(self):
        if not self.res:
            return
        path = filedialog.asksaveasfilename(title='Ведомости в Excel', defaultextension='.xlsx',
                                            initialfile=self.file_base() + ' — ведомости.xlsx', filetypes=[('Книга Excel', '*.xlsx')])
        if not path:
            return
        try:
            xlsx_io.write_xlsx(path, reports.all_sheets(self.res, self.project), self.project['meta'].get('objectName') or 'Таксация')
            self.status('Ведомости сохранены: %s' % path)
            if sys.platform == 'win32' and messagebox.askyesno(APP, 'Файл сохранён. Открыть его?'):
                os.startfile(path)  # noqa: S606 — открытие сохранённого пользователем файла
        except OSError as ex:
            messagebox.showerror(APP, 'Не удалось сохранить:\n%s' % ex)

    def show_help(self):
        messagebox.showinfo(APP, HELP)

    def on_close(self):
        if self.confirm_discard():
            self.destroy()


HELP = (
    'Кол-во — штук; для участка поросли — площадь, м²; для живой изгороди — длина, п.м. Пусто — 1 шт.\n'
    '№ нач. — начальный номер по плану, если нумерация с разрывом.\n'
    'Диаметр — целое число, см. Многоствольное: «20,15,12». Кустарник и изгородь: «-». Поросль деревьев: «0-4». '
    'Мелкие: «до1», «до4»; высаженные для озеленения: «**до1», «**до4».\n'
    'Действие — вырубить / пересадить / сохранить. Для вырубки обязательна причина.\n'
    'Инд. К — номера коэффициентов, меняемых для строки на обратные (например 24), или «без компенcации».\n\n'
    'Файлы проекта .json открываются и в веб-версии. Импорт из ТАКСАЦИЯ-АВТО читает .xlsm старой программы.')
ABOUT = (
    'Таксация Лайт — свободный аналог расчётов программы «ТАКСАЦИЯ-АВТО 4.2».\n'
    'Справочники (породы, тарифы, нормы посадок, таблицы объёмов, базовые величины) перенесены из исходного файла; '
    'расчёт сверен с исходной программой на тестовых наборах.\n'
    'Перед выпуском документации проверяйте тарифы и базовую величину по действующим нормативным актам.')


def main():
    if sys.platform == 'win32':
        try:
            import ctypes
            ctypes.windll.shcore.SetProcessDpiAwareness(1)
        except Exception:  # noqa: BLE001 — старые Windows без shcore
            pass
    path = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith('--') else None
    if '--selftest' in sys.argv:
        res = E.compute(example_project(), REF)
        print('selftest total =', E.money_text(res['removal']['money']['total'], res['settings'], res['baseValue']))
        return 0 if abs(res['removal']['money']['total'] - 243.375) < 1e-9 else 1
    app = App(path)
    app.mainloop()
    return 0


if __name__ == '__main__':
    sys.exit(main())
