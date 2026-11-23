# Eel-ditor — design principles and decisions

This document records *why* Eel-ditor is built the way it is, so that new work does not change fundamental design decisions.
When a change conflicts with a rule here, either follow the rule or change the rule here first, with the reason.

Each decision is written as **Decision** — **Why** — and, where useful, **Consequences** (what it forces elsewhere).

---

## 1. Core principles (in priority order)

1. **A clean git history comes first.** Every byte that changes in a file is a change the user made on purpose.
   The editor never "fixes" anything by itself: it does not pad tables in the source, reflow paragraphs, normalize
   line endings, trim whitespace or rewrite Markdown. Saving reproduces the file byte for byte (same encoding, BOM,
   line endings). Any feature that would produce noise in `git diff` is either rejected or made an explicit command.
2. **A plain-text editor first.** Markdown is edited as monospace text. Everything "rich" (hidden markers, aligned
   tables, grids, task boxes, image previews, Cyrillic view) is *display only*: the document text is never changed by
   rendering.
3. **No mixed modes.** The display is predictable: markers are always hidden (or always shown in raw mode); they are
   not revealed around the caret. A display mode that is read-only is fully read-only, not "editable with tricks".
4. **Many files, no tabs.** The open-files list scales to dozens of files; tabs do not.
5. **Diff and git inside the editor**, using the plain `git` command line, never replacing git itself.
6. **Fast and light.** Background work (git, search, diff, measuring) never blocks typing. Single executable,
   splash in a fraction of a second.
7. **Local and private.** Nothing is fetched from the network (images only from local files).
8. **Restrained look.** Classic menu, toolbar and scrollbars; one accent colour, otherwise greys; flat, lightly coloured
   icons; dark theme by default.
9. **Shortcuts: a compromise between Notepad++ and Visual Studio.** AltGr always types its character (§9).

---

## 2. Technology

**Decision:** C# / .NET 10, Avalonia 11, AvaloniaEdit, TextMateSharp for syntax highlighting, DiffPlex-style line diff
(own `LineDiff`/`WordDiff` in Core), `git` CLI.
**Why:** AvaloniaEdit's `VisualLineElementGenerator` and line transformers let the display differ from the document
(hidden `**`, virtual table padding) without touching the text — the foundation of principle 2. Scintilla cannot do
this. Avalonia is cross-platform. The git CLI needs no native libgit2 dependency and works with any installation on PATH.

**Own grammars:** languages missing from TextMateSharp are shipped as embedded `Syntax/*.tmLanguage.json` and
registered in `EelRegistryOptions` (currently AVCODE, `.avcode`, Aurora Vision programs).
**Why:** one executable, no files next to it. TextMateSharp's `GetGrammar` is an interface implementation that cannot
be overridden, so `EelRegistryOptions` re-implements `IRegistryOptions` and hides the language lookups with `new` —
always hold the `EelRegistryOptions` type, never plain `RegistryOptions`.

**Solution layout** (dependencies point downwards only):

| Project | Contents | UI? |
|---|---|---|
| `EelDitor.Core` | encodings, EOL, atomic save, file monitor, search, line/word diff, git wrapper, paragraph wrap, transliteration | no |
| `EelDitor.Markdown` | Markdown display model (line kinds, hidden ranges, styles, table padding), table layout/grid wrap, table and inline-style edits | no |
| `EelDitor.App` | Avalonia app: windows, menus, open-files panel, rendering pieces for AvaloniaEdit, diff view | yes |

Logic that can be tested without UI belongs in Core or Markdown. `MainWindow` is split into partial files by area
(`MainWindow.Files.cs`, `.Diff.cs`, `.Git.cs`, `.Search.cs`, `.CellEditor.cs`, …).

**Build output** goes to `bin/<Project>/` and `obj/<Project>/` next to `src`, not inside each project
(`Directory.Build.props`, `BaseOutputPath`/`BaseIntermediateOutputPath`). **Why:** `src` holds only sources. Not the
SDK's `UseArtifactsOutput` with the repository as `ArtifactsPath`: the SDK excludes everything under that path from
compilation. The paths apply only when no artifacts path is given, so `dotnet test --artifacts-path <dir>` still keeps
test builds away from the running app's folder.

---

## 3. Files, encodings, line endings

- **Byte-exact round trip.** `TextFile` detects BOM → valid UTF-8 → heuristic/ANSI code page, and stores encoding + BOM.
  Saving writes the same. Tests check round trips for UTF-8 (±BOM), UTF-16 LE/BE, single-byte code pages, mixed EOL,
  no final newline.
- **Line endings.** The dominant EOL becomes the document's EOL; new lines and pasted text get it. Mixed EOL shows a
  warning bar with an explicit "unify" action — never automatic. EOL conversion is an explicit menu command.
- **Encoding menu has two different operations:** "reopen as" (reinterpret bytes) and "convert to" (change how it is
  saved). Unencodable characters on save produce a warning listing the lines.
- **Atomic save** via a temp file and replace, keeping attributes.
- **Dirty marker (●)** compares the text with the file on disk shortly after an edit (files ≤ 4 MB), so typing and
  deleting a character by hand clears it. It is not derived from the undo stack alone.
- **External changes.** `FileMonitor` = one debounced `FileSystemWatcher` per directory + polling on window activation
  (watchers are unreliable on network drives and with rename-replace saves). Unmodified files reload automatically
  (setting, default on); modified ones show a bar (reload / keep mine / compare). Own saves are ignored.
- **Reload from disk** (F5, toolbar, file context menu) is undoable with Ctrl+Z.
- **Large files** (default ≥ 10 MB, `LargeFileThresholdMb`): no highlighting, no Markdown rendering, word wrap off by
  default. No memory-mapped mode for multi-GB files — out of scope.

---

## 4. Open-files panel (instead of tabs)

- Files grouped by directory; nested directories are nested in the tree. Group names are shortened from the left by
  **whole folders** (`…\src\EelDitor.Core\Git`, never cut mid-name); full path in a one-line tooltip.
- Filter box, Ctrl+Tab in most-recently-used order (commit on Ctrl release), Ctrl+P quick open.
- Git status per file (M, A, U untracked, D, C conflict) as a coloured letter, VS Code style; a group shows branch and
  change count. On the hovered row a ✕ takes the letter's place and closes that file (unsaved changes are asked about).
- Session (open files, caret positions) and recent files are restored at start; window position is remembered (so a
  window snapped to half the screen comes back there).
- The sidebar has tabs: Open files, Git, Map (document map). The first is named "Open files", not "Files", so it is
  not mistaken for a browser of the disk. Paths (also in the Git tab) are shortened by whole leading folders.
- "Display as ›" in the file context menu replaces a global Language menu.
- **The current file's header** (name and folder above the editor) is larger than the rest of the chrome (name 15 px,
  folder 12.5 px, 34 px high): the open file is the reference point of the whole window, including the Git and Map
  tabs, so it must stand out.

---

## 5. Markdown display

**Model.** `MarkdownDocument` (Markdown project) parses line by line into `LineRender`s: hidden ranges, styled ranges,
table padding, `<br>` breaks, images. It is a pragmatic subset of CommonMark + GFM tables + extensions chosen by the
user: task lists, images (local only), highlight `==…==`, footnotes, subscript `~x~` and superscript `^x^`
(Pandoc / markdown-it syntax: single markers, no spaces inside; `~~x~~` stays strikethrough). A few HTML tags that AI
tools and GitHub users write are read as styles: `<br>` (any form, a line break in table cells), `<sub>`, `<sup>`,
`<s>`, `<del>`, `<mark>`; other HTML is plain text.

**Emphasis rules** follow CommonMark 0.31, where punctuation includes symbols (`< > = + | ~ $ ^`): `**(a)**<br>`
closes the bold (.NET's `char.IsPunctuation` alone does not count `<`).

**Hidden markup.** Markers (`**`, `_`, `` ` ``, `#`, link targets) are replaced by `HiddenElement`s: zero width and
**zero height** (a full-height empty element made lines taller and broke scroll height). The caret jumps over a
marker as one unit. `WholeElement` keeps **one visual column per source character** so AvaloniaEdit's hit-testing
stays aligned; the caret skips the inside.

**Styles keep the monospace font.** Bold/italic never change character width, so alignment holds. The default font
must have a real bold face (Consolas; Cascadia's variable bold is not drawn, so it is replaced once).
Exceptions, where nothing has to line up: **heading levels differ in size** (# 1.5×, ## 1.3×, ### 1.15×, deeper levels
as the text; all bold in the accent colour), and sub/superscripts (and footnote references) are 0.75× and shifted. In a
grid cell a sub/superscript is drawn smaller inside its own character cells, so the grid stays aligned.

**Raw mode** (toolbar, Ctrl+Shift+M) turns all of this off. Clipboard always carries source text with markers;
copying a bold fragment must not produce visible stray stars on paste.

**Editing at style boundaries** (decisions made after bug reports):
- Backspace/Delete at a style boundary removes the whole style marker pair, never one star of `**`.
- Typing right after a styled letter extends that style; adjacent identical spans are merged while typing, so the
  source never ends up as `**Prost****y**`.
- Ctrl+B / Ctrl+I / Ctrl+Shift+X / Ctrl+` toggle markers via `InlineStyleEdit`, which is **verified before it is
  applied**: the line must show exactly the same characters afterwards, the whole range must gain/lose the style, and a
  table row must stay the same table row. If verification fails, that range is left unchanged. On a selection over
  table rows the style is applied per cell, never across `|`.
- Edit → Convert style (highlight ↔ bold ↔ italic) rewrites only the markers of each span (`==a==` → `**a**`), in the
  selection or the whole file, as one undo step; each span is verified the same way and skipped (and counted) when
  the change would alter what the line shows. Code blocks and code spans are not touched.
- **Clear formatting** (Ctrl+Space, Markdown menu, context menu) removes every inline style and link from the
  selection (or the spans at the caret). Part of a span is taken out of it (`**ab**cd**ef**`); a link goes only as a
  whole, keeping its text. Headings and lists are block formatting and stay.

**Lists** (`ListEdit`): Enter in an item starts the next one (the rest of the line moves into it), Shift+Enter adds a
line inside the item indented to its text, Enter on an empty item ends the list. Ctrl+Enter / Ctrl+Shift+Enter (and
the menus) insert an item below / above. An ordered list is renumbered only when it is numbered in sequence; a list
written as `1. 1. 1.` keeps its numbers (fewer changed lines). Wrapped list items cannot get a hanging indent: Avalonia's
text formatter ignores paragraph indentation (verified; AvaloniaEdit's `WordWrapIndentation` has no effect), see §13.

**Renumber items** (list menus, only in an ordered list) numbers the caret's list 1…N, keeping its delimiter and
indentation; nested lists keep their numbers. It is an explicit command because it may change many lines, which
editing never does by itself (see above).

**Links.** Besides `[text](url)`, bare addresses (`https://…`, `http://…`, `www.…`, `mailto:…`, GFM autolink rules:
trailing punctuation and an unbalanced `)` are not part of the address) and autolinks `<https://…>`, `<a@b.org>` are
links: underlined in the accent colour, opaque to emphasis (`a_b_c` in a URL stays as written), opened by Ctrl+click
(a plain click places the caret, as in Visual Studio). Outside the Markdown view (plain text, code, raw mode)
`PlainLinks` underlines and opens bare addresses the same way. Only web and mail addresses go to the system; paths
open in the editor; nothing else is executed. AvaloniaEdit's own hyperlinks stay off (they would conflict with
hidden markup and open any URI scheme).

**Horizontal rules** (`---`, `***`, `___`; not a setext underline, not YAML front matter) are drawn as a line across
the whole view; Backspace or Delete anywhere on it removes the whole line.

**Whitespace display in a grid.** With word wrap on, tables are grids; CR/LF markers are not drawn inside the grid
(the grid has no source columns to put them in).

---

## 6. Tables

**Virtual alignment (no wrap).** `TableLayout` computes per-column widths from *shown* text (hidden markers removed,
East Asian wide characters count as two) and `PaddingGenerator` inserts virtual spaces (dashes in the separator row)
before pipes. Padding is merged with a neighbouring real character so the caret never stops inside padding. After a
cell's text it goes at the cell's last space before the pipe, so a space typed after the text shows where it was typed
(merged with the first space, the caret jumped past the padding). The source
keeps its original spacing.

**Grid (word wrap on).** A table row is drawn as an ASCII-style box grid (`TableRowElement`, one element for the whole
line) with text wrapped inside cells (`TableWrap`). ASCII-style borders were chosen over continuous lines because the
continuous version misaligned text in the first rows. Column widths: natural widths when they fit; otherwise narrow
columns keep their width and wide ones share the rest.
- A grid row is **not editable in place** (one source line ↔ many drawn lines). Editing goes through the cell editor
  (double click / command). The caret on a grid row is hidden (`CaretBrush` transparent) and **the current cell** — the
  one whose source contains the caret — is outlined instead: cells are what operations act on (editing, Tab). A click
  puts the caret into the clicked cell's text. The outline lies on the grid's own lines (the middle of the │ borders and of
  the +---+ lines above and below, measured where the neighbouring rows really are), drawn in a layer above the text,
  so nothing — text or the in-place editor — overlaps it, and neighbouring frames share their edges; the editor's current-line band is turned off on grid rows (it covers the whole
  row element including its bottom border, which looked shifted down).
- **Moving between cells like in a spreadsheet:** on a grid row the arrows go from cell to cell (Left/Right stop at
  the row's edge to the previous row's last / next row's first cell, Up/Down keep the cell index), the separator is
  skipped and past the table's edge the caret goes to the line before/after it; Enter edits the current cell. A double
  click edits the cell with the caret where it was clicked (nothing selected); F2 and Enter select the cell's text. Without this the row, being one element, sent the caret to its first or last cell.
- **Images in tables are shown as their text** (italic) in every view — as text, as a grid and in the cell editor —
  because a grid cell cannot hold a preview; before, the preview appeared in some views and not in others.
- **Selecting text in a grid** is a separate display-only selection (`GridTextSelection`): drag inside one cell selects
  its shown text, across cells selects whole cells; Ctrl+C copies shown text (several cells: TSV, one line per row).
  Select column / Select row use this selection in a grid (the source ranges the text mode highlights are not drawn there).
- **Commands act on the selected cells.** Styles (Ctrl+B…, highlight), clear formatting and convert style apply to the
  cells selected by Select column / row or by dragging in a grid (part of one cell maps back through the hidden
  markup), and the selection stays, so the next command applies to the same cells. In a grid without a selection they
  apply to the current cell. A header cell is bold as a whole in the model (as the grid draws it), so adding a style
  there is not mistaken for a change of its boldness.
- **Selected cells are outlined** on the grid lines, like the current cell, not highlighted: the text stays readable and its styles
  visible. Part of one cell's text is highlighted as text.
- **Editing a grid cell looks like editing in place**: the cell editor has no frame and the cell's background, lies
  exactly over the cell's text (inside the current cell's outline), wraps at the cell's width and is as tall as the
  text. Every change is written into the row at once, so the row and its neighbours follow the typing; the whole edit
  is one undo step, and Esc puts the row back exactly as it was. A change that would break the row is not written and
  is reported on the status bar. Spaces typed after the text (not part of the cell's width yet) widen the editor too,
  so they do not wrap to the next line. There are no hints in the editor. True in-place editing (no editor at all) would need
  a caret, selection, input and IME of our own for grid rows — AvaloniaEdit lays a line out as one flow of elements,
  and cells side by side cannot be expressed — so it is not done.
- **Clipboard:** copying selected cells puts Markdown rows (plain text; whole rows exactly as written, a header with a
  separator) and an HTML table (Windows "HTML Format", others text/html) for Word and Excel. Pasting in a table inserts
  rows above the current row (below the separator when on the header): from Markdown rows, an HTML table (Word, web)
  or tab-separated text (Excel), fitted to the table's columns; outside a table a pasted HTML table becomes a new
  Markdown table. Cut of whole body rows removes them. `TableClipboard` holds the conversions. A pasted table's first row becomes the
  header only when the source marked it as one (`<th>`/`<thead>`, or a Markdown separator); otherwise the new table
  gets an empty header and every pasted row stays a body row.
- **Split table** (at a body row): a blank line ends the first table, the second starts with copies of the header and
  the separator (column names and alignment stay).
  The document selection is not used because it can only take whole rows.

**Structural edits — minimal diff** (`TableEditor`): new cell `|  |` (separator `| - |`), existing cells are never
re-padded, deleting a column cuts only its segments. Physically aligning a table in the source is an explicit command
("Format table") only.

**Cell editor** (`MainWindow.CellEditor.cs`): a popup editor sized so a scrollbar is rarely needed (up to ~110
characters wide, up to 75 % of the view tall). It shows the cell's Markdown without hidden quotes or escapes added for
display; style shortcuts work inside. Saving goes through `TableCellEdit.ApplySource`, which guarantees the table is
never broken: bare `|` escaped, backticks that would swallow following cells escaped, trailing backslash doubled; the
row must keep the same number of cells and the other cells unchanged — otherwise nothing is written and the reason is
shown. Line breaks (Shift+Enter) are saved as `<br>`; an unchanged cell keeps its tags as written. Hints in the editor
are minimal (no style hints).

**Alt+Q / long-line wrap never touch tables.** Long table rows are left long; with word wrap on they are shown as a grid.

---

## 7. Line breaking of paragraphs (git-friendly)

**Decision:** paragraph wrapping applies to **new text only**. Modes: one line (default), 80, 120, one sentence per
line (semantic line breaks). Settings are global; Markdown and plain text only, never code.
**Why:** reflowing existing paragraphs rewrites many lines and pollutes diffs. Semantic line breaks are the most
diff-friendly mode.
- While typing, only the line being typed is broken when it crosses the limit; following lines are never reflowed.
  A paragraph that was already one long line stays one line.
- Sentence mode breaks after `.`, `!`, `?`, `…` followed by a capital, but not after abbreviations (np., m.in., e.g.),
  initials, or inside inline code.
- **A broken line never starts with something Markdown would read as a block** (`#`, `-`, `1.`, `>`, `|`, ```` ``` ````),
  and breaks never split an inline span (stars would show). Headings, tables, code, HTML and YAML front matter are skipped.
- Alt+Q reflows the paragraph(s) at the caret, on request only.
- **Long lines are not a problem to warn about.** One line per paragraph is a legitimate style, and lines over
  AvaloniaEdit's 3000-character limit render fully (§12). There is no warning bar.
- **The convention is a key Markdown setting, so it is on the toolbar** (prose files only):
  - "Line breaks at edit:" — a combo box with the setting for new text: Disabled / 80 / 100 / 120 columns / After each
    sentence. The toolbar has the short names; the full ones ("Disabled (one line per paragraph)", "Break after each
    sentence") are in the tooltip and in Edit → Line breaks at edit. The default is Disabled. Word wrap (View) is on by
    default, so one-line paragraphs and wide tables fit the window.
  - "Current file:" — a drop-down button with the file's actual convention in short words ("One line per paragraph ▾",
    "Sentence per line ▾"), detected by `ParagraphWrap.Detect` (the way at least 60 % of the telling paragraphs
    follow; otherwise "Mixed"). When it differs from the setting, a ⚠ sign is shown.
  - Its menu holds the actions: "Use for new text: …" and "Convert to one line per paragraph / 80 / 100 / 120 columns /
    sentence per line" (not to the convention the file already has). Converting to a width or to sentences splits long
    lines; converting to one line per paragraph joins the lines of each paragraph (an explicit command, so allowed).
    URLs are never cut and no word changes; a "Show word diff" button proves it. When there is nothing to convert,
    nothing changes and only the status bar says so (no "split 0 lines" notice).
  - The status bar shows "Longest line: N" for Markdown files. Detection runs 600 ms after typing stops.
- **Long lines are read with the editor's word wrap** (View → Word wrap), which wraps paragraphs on screen and draws
  tables as grids. A **soft "hybrid" wrap** (breaks at 120 kept in the buffer with an invisible marker, joined on save)
  was assessed and **dropped by the user's decision** in favour of word wrap: it would need a mapping between editor
  and file lines in every feature (diff, change bars, search, go to line); if ever revisited, keep the logical text in
  memory rather than a shadow `.tmp` file (untracked file in git, sync tools, stale after a crash).

---

## 8. Diff and git

- **Diff is a view mode of the current document**, not a separate window. Each side is a live document (editable,
  recomputed as you type) or read-only text (HEAD, index, disk version, a commit). Copy blocks left/right and revert
  change/all, like WinMerge. Synchronized scrolling on by default.
- Diff commands appear on the **main toolbar** after a separator only while comparing (no separate diff toolbar);
  commands that make no sense outside a comparison are disabled. Menu items and toolbar buttons are greyed out when
  not applicable — everywhere.
- **Bottom line panel** shows the current line from both sides across the full window width with changed characters
  highlighted; resizable by dragging.
- **Word diff** (`git diff --word-diff` semantics: a word is a run of non-whitespace) is one read-only text with
  deleted/inserted words; it follows edits of the working copy. Lines are compared first, words only inside changed
  blocks. It shows that re-breaking lines changed no words. The word view is always wrapped (one text, nothing to
  align), the change F7 / Shift+F7 went to is outlined, and the bottom line panel is hidden there (the word view shows
  both versions already). Switching between the modes keeps the place (the current block's change, or the caret's line).
- **Next / previous change brings the change itself into view:** the caret goes to the block's first changed character
  (the common beginning of the two lines when a line is too long for the character diff) and the view scrolls
  horizontally to it. With one line per paragraph a change is often far to the right; going to the line's start or only
  moving the caret looked like the command did nothing.
- **Closing never closes the compared file by surprise:** while a file is compared, the first close (Ctrl+W, ✕ in the
  file list, its menu, a middle click) ends the comparison and only the next one closes the file; the file header's ✕
  is hidden then, leaving only the comparison's ✕.
- While comparing, only Undo/Redo of the editing commands are allowed; they go to the editable side (so "Revert all"
  is undone by Ctrl+Z, also in word mode).
- **Git panel:** unstaged/staged lists, commit message, Rescan / Commit / Push. File context menu: log, diff vs HEAD,
  stage/unstage, revert (with confirmation), remove from repo = `git rm --cached` (the safer variant: the file stays on
  disk). No credentials are requested; push relies on the user's credential manager.
- **Merge conflicts:** marker blocks highlighted, a bar to move between them and resolve (current / incoming / both),
  then offer `git add`. Git features are disabled outside a repository.
- `GitService` runs git at below-normal priority, results cached per repository and refreshed after save, on
  activation and when `.git/index`/`HEAD` change.

---

## 9. Commands, shortcuts, localization

- Every user action is an `AppCommand` (id like `file.reload`, `edit.undo`) reachable from menu, toolbar and keymap,
  with Visual Studio-style two-step chords supported.
- **AltGr always types.** On Windows AltGr = Ctrl+Alt; when Ctrl+Alt+key produces a character in the current layout,
  the character is typed and no shortcut fires (`KeyMap.IsAltGrText`). Commands therefore have non-Ctrl+Alt shortcuts
  (Ctrl+Shift+…, F-keys); Ctrl+Alt remains only as an alias and for non-character keys (arrows, Backspace, Delete).
- Shift+Insert / Ctrl+Insert work as paste/copy.
- **All UI text goes through `Loc`** (`Lang/<code>.json`: `pl`, `en`, `isv`, `isv-Cyrl`). Missing keys fall back to English,
  then Polish. A test checks that keys and `{0}` placeholders match Polish. Interslavic notes are in `Lang/isv-notes.md`.
  `isv-Cyrl.json` is generated from `isv.json` (`py tools/make_isv_cyrl.py`; official Cyrillic alphabet, git terms, key
  names and Markdown syntax stay Latin): edit `isv.json` only, then regenerate.
  Interslavic UI rules (user's decisions): buttons and menu items are imperatives (*Anuluj*, *Kopiuj*, *Vstavi*),
  never infinitives (infinitive only in yes/no questions); *Kopijuj* (dictionary *kopijovati*, not *kopiruj*); *Izsěči* for Cut; *iz diska*
  (from the disk, not *s diska*); *vo vsih* before *vs-*; verbal nouns of the imperfective for ongoing actions
  (*pisanje, vstavjanje, odstranjanje*); strikethrough is *izčrknuty* (a dictionary word; *prěčrknuty* does not exist).
  Labels are short; explanations (including Markdown syntax such as `==text==`) go into tooltips, never into the label.
  Buttons on info bars are a word or two ("Reload", "Hide"). Info bars are for things that need a decision (file
  changed on disk, mixed EOL, lossy decoding), not for styles the user may well want (long lines).
- **Everything shown about the text follows the text, including Ctrl+Z.** A notice about a result ("Split 8 long
  lines…") goes as soon as the text changes; state derived from the text (line endings, the file's line-break
  convention, the longest line, conflicts, the dirty marker) is recomputed from the text after edits (debounced),
  never kept from the last command. Choices the user made ("Leave as is" for mixed endings) are kept.
- Comparison commands say what they do: "Diff vs HEAD", "Diff vs version on disk", "Diff vs another file…".
- **Menus are shallow where work happens often:** tables have a top-level Table menu; the Markdown menu lists the
  heading levels in place (no submenu); the plain-text view of Markdown is a View option.
- **One place per setting:** Preferences holds only what has no menu (theme, language, font, large files); tab width,
  spaces and automatic reload live in the Settings menu only. Every window and dialog uses the app's grey background
  (`ChromeBrush`), never the theme's near-black default.
- Window title: app name first, then the file name.

---

## 10. Cyrillic

- **Convert** (Edit → Latin ↔ Cyrillic): Russian, Ukrainian, Serbian, Interslavic and Polish Cyrillic A/B
  ("Polska cyrylica 2.0"), in one list without separators. Changes the text (undoable).
- **Show Latin text in Cyrillic** (Encoding menu): the same alphabets, display only; the file is unchanged and **the
  editor is read-only** while it is on (editable Cyrillic was tried and withdrawn: a mixed mode). **Markdown
  formatting stays**: the Cyrillic generator runs after the Markdown ones, leaves hidden markup and code in Latin,
  cuts words where a style changes, keeps each word as wide as its Latin source in table rows (alignment), and grid
  cells are converted through `MarkdownView.WordTransform`.

---

## 10a. Export (HTML, PDF)

- File → Export to HTML / PDF. The HTML comes from **the editor's own Markdown parser** (`MarkdownHtml`), so the export
  shows what the editor shows (same emphasis rules, tables, `<br>`, footnotes, tasks); no Markdown library is added.
  Blocks: headings, paragraphs, nested lists, quotes, fenced code, tables, rules. Raw HTML is escaped. Non-Markdown
  files are exported as preformatted text.
- **PDF is printed by a Chromium browser in headless mode** (Edge on Windows, Chrome/Chromium elsewhere) with a
  throw-away profile: best layout of tables and text, no PDF library. Without such a browser the user is told to
  export HTML instead.
- Privacy: the page has a Content-Security-Policy allowing only local files (`img-src file: data:`), so remote
  images are not fetched; relative image paths resolve through `<base href>` to the document's folder.

## 10b. Preview (HTML and Markdown)

- View → HTML preview (Ctrl+Shift+V, as in VS Code) shows the current file rendered **on the right of the text**: an
  HTML file as itself, a Markdown file through `MarkdownHtml` (the same HTML as the export). It follows typing after
  300 ms and keeps its scroll position; its width and visibility are remembered. Trial since 2026-11-23.
- **Drawn by Eel-ditor itself, not by a browser engine:** `HtmlParser` (Core, forgiving, implied end tags, entities)
  builds a tree and `HtmlRenderer` turns it into Avalonia text blocks, lists, tables (colspan/rowspan), code blocks,
  quotes and images. **Why:** principle 6 (no embedded browser, single light executable) and 7 (nothing runs, nothing
  is fetched). Consequences: scripts, CSS (except `text-align`) and forms do not work; iframes, video and SVG are a
  placeholder; images only from local files or `data:` URIs.
- It is a separate read-only view beside the text, never a mode of the text (principle 3); text in it can be
  selected and copied. A click on a link opens it (no caret to place, so no Ctrl), `#anchor` links scroll the
  preview, other links go through the same rules as Ctrl+click in the editor. Documents over 3 MB are not previewed.

---

## 11. Performance and startup

- **The Markdown model is reparsed lazily**, once per edit or per document update (commands, undo, redo run inside
  one update), not once per changed line: a column command on an 800-row table used to reparse 300 KB 800 times (and
  freeze for seconds, also on undo). Commands that change many lines take their line parsers before the update and
  never read the model inside it. A test keeps column commands on such a table under a few seconds.

- Single-file executable (self-contained publish) + native splash shown before Avalonia starts; `Warmup` prepares
  non-UI objects (code pages, TextMate registry) on a background thread.
- Files load and decode in the background.
- `LineHeightPrimer` measures lines of non-default height (grid rows, images, styled lines) in small background
  steps so the scrollbar is right from the start and the caret does not outrun the thumb. The first visible line is
  kept in place while heights above it change.

---

## 12. AvaloniaEdit workarounds (read before touching rendering)

| Problem | Workaround |
|---|---|
| `TextEditor.ScrollToVerticalOffset`/`Horizontal` do nothing in 11.4 | `EditorScroll` sets the inner ScrollViewer's offset |
| Lines longer than `VisualLine.LENGTH_LIMIT` (3000) run no element generators | `LongLineGenerators` (first line transformer) rebuilds the elements with AvaloniaEdit's own algorithm and `ReplaceElement`; text run properties set via the internal `SetTextRunProperties` by reflection; if that is missing, the colorizer's fallback collapses markers |
| Elements covering N chars with 1 column shift hit-testing | `WholeElement`: one visual column per source character |
| Line heights are known only after drawing | `LineHeightPrimer` |
| `TextView.Bounds` holds the old width while a resized view builds its lines | grids take the wrap width from the private `_lastAvailableSize` (`WrapWidth`), set before any line is built; with Bounds every resize showed the previous width's columns and rows built at other times differed (a jagged table) |
| Table rows are repainted often (scrolling, caret, height primer) | `TableRowRun` shapes its text once and keeps it until the row is rebuilt |
| `Application.FindResource` cannot see theme dictionaries | `Themed` binds code-built controls to theme resources |

---

## 13. Rejected or deferred (and why)

| Idea | Status | Reason |
|---|---|---|
| WYSIWYG Markdown editor (rewrite from a model) | rejected | rewriting the file normalizes markup → diff noise; violates principle 1 |
| Revealing markers around the caret (Typora/Obsidian style) | rejected | "mixed mode", same reason editable Cyrillic was withdrawn |
| Editable Cyrillic view | withdrawn | the user did not accept the combined behaviour |
| Read-only formatted preview (Markdig → Avalonia controls) | done differently | own HTML renderer, see §10b |
| PDF preview (read-only) | possible later | not requested yet |
| Markdig (or another Markdown library) for export | not used | the export must match the editor's parser; adding packages needs the user's approval |
| Single `~x~` as strikethrough (GitHub) | rejected | `~x~` is subscript (Pandoc, markdown-it); strikethrough is `~~x~~` |
| Soft "hybrid" wrap with marker characters | dropped | word wrap does the job; see §7 |
| Hanging indent for wrapped list items | not possible directly | Avalonia ignores `Indent`; a workaround would insert virtual indent elements at the break points found by a first layout (relayout until stable) — fragile, not done |
| A warning bar for long lines | removed | one line per paragraph is a valid style; the toolbar shows the file's convention |
| Auto-aligning tables in the source on save | rejected | only as the explicit "Format table" command |
| Memory-mapped multi-GB files | out of scope | target is up to a few hundred MB |
| libgit2 | rejected | native dependency; git CLI is enough |
| Network images | rejected | privacy |

---

## 14. Quality and testing

- xUnit for Core and Markdown; Avalonia headless tests for the App (`[AvaloniaFact]`), driving real key and mouse
  input where behaviour depends on it.
- Every reported bug gets a regression test that fails without the fix.
- Table and inline-style edits have randomized tests ("never breaks the table").
- `RandomEditingExplorer` (enabled with `EELDITOR_EXPLORE=1`) types random keys into Markdown documents and reports
  states where visible markers or broken tables appear.
- Tests must not touch the user's settings: `SettingsStore.UseDirectory(TestDirs.New())`.
- Unexpected exceptions that did not stop the program go to `%APPDATA%/Eel-ditor/errors.log`.
