"""The single entry point: choose a card, its language and the gold frame."""
import json
import subprocess
import sys
import threading
import tkinter as tk
from tkinter import messagebox, ttk
import unicodedata
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from common.render.paths import CARDS, LOGS, PLAYER, REPO, ROOT  # noqa: E402

LANGUAGE_LABELS = dict(fr_FR='Français', en_US='English', ja_JP='日本語', it_IT='Italiano', de_DE='Deutsch', es_ES='Español', pt_BR='Português (Brasil)', ko_KR='한국어', zh_TW='繁體中文')


def open_card(card_id, language='en_US', gold=False):
    """Start the player on an already compiled card."""
    if not PLAYER.is_file():
        raise ValueError('Build the player first: unity/tools/build_player.ps1')
    archive = CARDS/f'{card_id}.zip'
    if not archive.is_file():
        raise ValueError(f'Card not available: {card_id}')
    with zipfile.ZipFile(archive) as packed:
        card = json.loads(packed.read('card.json'))
    if language not in card['languages']:
        raise ValueError(f'Language not available: {language}')
    if gold and not card['hasGold']:
        raise ValueError(f'There is no gold frame for {card_id}.')
    LOGS.mkdir(parents=True, exist_ok=True)
    arguments = [str(PLAYER), '--native-rendergraph', '--project-root', str(ROOT), '--card', card_id, '--language', language,
                 '-force-vulkan', '-logFile', str(LOGS/'player.log')]
    if gold:
        arguments.append('--gold')
    return subprocess.Popen(arguments)


def folded(text):
    return ''.join(c for c in unicodedata.normalize('NFD', text.casefold()) if unicodedata.category(c) != 'Mn')


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title('Pokémon Pocket card viewer')
        self.geometry('640x560')
        self.minsize(520, 420)
        self.language = tk.StringVar(value='en_US')
        self.gold = tk.BooleanVar()
        self.query = tk.StringVar()
        self.cards = {}
        self.visible = []
        frame = ttk.Frame(self, padding=12)
        frame.pack(fill='both', expand=True)
        ttk.Label(frame, text='Card (name or id)').pack(anchor='w')
        entry = ttk.Entry(frame, textvariable=self.query)
        entry.pack(fill='x', pady=(2, 8))
        entry.focus()
        self.query.trace_add('write', lambda *_: self.refresh())
        listing = ttk.Frame(frame)
        listing.pack(fill='both', expand=True)
        self.tree = ttk.Treeview(listing, columns=('name', 'rarity', 'id'), show='headings', selectmode='browse')
        for column, title, width in (('name', 'Name', 300), ('rarity', 'Rarity', 70), ('id', 'Id', 170)):
            self.tree.heading(column, text=title)
            self.tree.column(column, width=width, stretch=column == 'name')
        scroll = ttk.Scrollbar(listing, command=self.tree.yview)
        self.tree.configure(yscrollcommand=scroll.set)
        self.tree.pack(side='left', fill='both', expand=True)
        scroll.pack(side='right', fill='y')
        self.tree.bind('<<TreeviewSelect>>', lambda _: self.update_gold())
        self.tree.bind('<Double-1>', lambda _: self.launch())
        options = ttk.Frame(frame)
        options.pack(fill='x', pady=8)
        ttk.Label(options, text='Language').pack(side='left')
        codes = {label: code for code, label in LANGUAGE_LABELS.items()}
        chooser = ttk.Combobox(options, values=list(codes), state='readonly', width=20)
        chooser.set(LANGUAGE_LABELS[self.language.get()])
        chooser.pack(side='left', padx=(8, 0))

        def choose(_):
            self.language.set(codes[chooser.get()])
            chooser.selection_clear()
            self.refresh()
        chooser.bind('<<ComboboxSelected>>', choose)
        self.gold_box = ttk.Checkbutton(options, text='Cadre gold', variable=self.gold)
        self.gold_box.pack(side='left', padx=(24, 0))
        actions = ttk.Frame(frame)
        actions.pack(fill='x')
        self.open_button = ttk.Button(actions, text='Show the card', command=self.launch)
        self.open_button.pack(side='left')
        self.update_button = ttk.Button(actions, text='Update the game', command=self.update_game)
        self.update_button.pack(side='right')
        self.status = tk.StringVar()
        ttk.Label(frame, textvariable=self.status, wraplength=600).pack(anchor='w', pady=(8, 0))
        self.bind('<Return>', lambda _: self.launch())
        self.load()

    def load(self):
        path = CARDS/'index.json'
        if not path.is_file():
            self.status.set('No card yet: click “Update the game”.')
            return
        index = json.loads(path.read_text(encoding='utf-8'))
        self.cards = index['cards']
        failures = len(index.get('failures', {}))
        self.status.set(f"Game {index['version']} · {len(self.cards)} cards" + (f' · {failures} not supported' if failures else ''))
        self.refresh()

    def name(self, card):
        return card['displayNames'][card['languages'].index(self.language.get())]

    def refresh(self):
        wanted = folded(self.query.get().strip())
        self.tree.delete(*self.tree.get_children())
        shown = 0
        for card_id, card in self.cards.items():
            name = self.name(card)
            if wanted and wanted not in folded(name) and wanted not in card_id.casefold():
                continue
            self.tree.insert('', 'end', iid=card_id, values=(name, card['rarity'], card_id))
            shown += 1
            if shown >= 500:
                break
        children = self.tree.get_children()
        if children:
            self.tree.selection_set(children[0])
        self.update_gold()

    def selected(self):
        selection = self.tree.selection()
        return selection[0] if selection else None

    def update_gold(self):
        card_id = self.selected()
        allowed = bool(card_id and self.cards[card_id]['hasGold'])
        if not allowed:
            self.gold.set(False)
        self.gold_box.state(['!disabled'] if allowed else ['disabled'])

    def launch(self):
        card_id = self.selected()
        if not card_id:
            return
        try:
            open_card(card_id, self.language.get(), self.gold.get())
        except ValueError as error:
            messagebox.showerror('Card unavailable', str(error))

    def update_game(self):
        self.update_button.state(['disabled'])
        self.status.set('Updating…')

        def work():
            command = [sys.executable, '-m', 'common.render', 'update']
            process = subprocess.Popen(command, cwd=REPO, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace', creationflags=subprocess.CREATE_NO_WINDOW)
            last = ''
            for line in process.stdout:
                last = line.strip() or last
                self.after(0, self.status.set, last)
            code = process.wait()
            self.after(0, self.finish_update, code, last)
        threading.Thread(target=work, daemon=True).start()

    def finish_update(self, code, last):
        self.update_button.state(['!disabled'])
        if code:
            messagebox.showerror('Update failed', last)
        self.load()
        if not code:
            self.status.set(last)


def main():
    App().mainloop()


if __name__ == '__main__':
    main()
