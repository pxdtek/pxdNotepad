# pxd Notepad

A light, fast notes app for Windows: instant search across every note, categories inside categories, rich text
(fonts, sizes, colours, highlight, lists), pictures you can resize and mark up with arrows and boxes, line numbers that
match each line's size, dark and light themes, and encrypted sync between your PCs.

![pxd Notepad, dark](docs/screenshot-dark.png)

## What it does

- **Search as you type, across everything.** Every 3+ letter piece of a word is in a trigram index (SQLite FTS5), so
  `zanzib` finds *zanzibar* in 20,000 notes in about 2 ms. Matches are highlighted in the list; opening a result
  selects the match in the note. `Ctrl+F` to search, `↓` / `Enter` to pick, `Esc` to clear.
- **Categories and sub-categories**, as deep as you like. Drag notes onto a category to move them, drag categories
  onto each other to nest them. A category shows its own notes and its sub-categories' notes. Deleting a category
  never deletes notes: they move up a level. Trash keeps deleted notes until you empty it.
- **Rich text, per part of a note:** font, size, colour, highlight, bold, italic, underline, strikethrough, bulleted
  and numbered lists, alignment and indent. Select text and pick, or pick first and type.
- **Line numbers** (`Ctrl+Shift+L`): each number is drawn at the size of the text that starts its line and sits on
  that text's baseline, so a 32 pt heading gets a 32 pt number. Wrapped lines keep one number.
- **Pictures:** paste a screenshot, drop or insert a file. Drag the corner to resize (it keeps its shape);
  right-click for actual size, half size or fit to the page.
- **Mark-up:** double-click a picture to draw arrows and boxes on it, in any colour and thickness (Shift for a square;
  move or delete marks later). The original is kept, so marks can always be changed or removed.
- **Dark and light** themes, switched from the title bar.
- **Sync** (Settings → Sync): notes, categories and pictures stay the same on every PC. Everything is encrypted on the
  PC with your own sync key (AES-256-GCM) before it's sent, so the server only stores data it can't read. If the same
  note is changed on two PCs, the newer edit wins.

![Mark-up](docs/screenshot-markup.png)

![Light theme](docs/screenshot-light.png)

## Keys

| Keys | Does |
|---|---|
| `Ctrl+N` | New note in the selected category |
| `Ctrl+Shift+N` | New category |
| `Ctrl+F` | Search |
| `Ctrl+B` / `I` / `U` | Bold / italic / underline |
| `Ctrl+]` / `Ctrl+[` | Bigger / smaller text |
| `Ctrl+Shift+L` | Line numbers on/off |
| `Ctrl+S` | Save now (it also saves by itself as you type) |
| `F2` / `Del` on a category | Rename / delete it |
| `Del` on a note | Move it to Trash |

## Sync

The first PC turns sync on with a one-time invite code from whoever runs the server. That creates your vault and a
sync key. Every other PC joins with the sync key (Settings → Sync → *I have a sync key*). Keep the key somewhere safe:
anyone with it can read your notes, and without it they can't be recovered.

The default server is `https://pxdtek.com` (its `/n/v1` API keeps each vault's encrypted records and images). Any server
that speaks the same API can be used instead.

## Where things are

- App: `%USERPROFILE%\Apps\pxdNotepad`
- Notes: `%APPDATA%\pxdNotepad\notes.db` (SQLite), settings in `settings.json` next to it. The sync key is kept
  encrypted for your Windows account (DPAPI).

## Building

.NET 10 SDK, Windows.

```
dotnet publish -c Release -o dist
dist\pxdNotepad.exe --self-test <out-folder>
```

The self-test checks storage, search speed, the note format, line numbers, images, mark-up and encryption, and draws
the window to PNG files in the out folder. Nothing appears on screen. Set `PXDNOTEPAD_TEST_SERVER` and
`PXDNOTEPAD_TEST_INVITE` to also run two copies syncing through a sync server.

Built with WPF (its rich-text editor is what makes mixed fonts, inline pictures and per-line number sizes possible)
and SQLite. Follows the pxd App Standard: own title bar, remembered window, single instance, self-update from GitHub
releases.
