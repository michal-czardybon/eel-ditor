# Release notes

## 0.3.3

- **NEW:** Shift-click and Shift+arrow select rectangular blocks in wrapped tables. Ctrl-click starts a fresh cell selection or opens the link under the pointer.
- **NEW:** Paste overwrites cells from the top-left of the selection or from the current cell. The table grows only where the pasted block runs past its edges.
- **NEW:** Delete clears selected cell contents and leaves the cells selected.
- **FIXED:** Copy uses the selected cell, row, column, or block. Pasting a copied body row outside a table creates a standalone Markdown table and preserves formatting.
- **FIXED:** Wrapped cells resize before layout as you type, avoiding a brief wrap at the old width. Up and Down use the caret's new visual column after typing in a multiline cell.
- **FIXED:** Remove Rows and Remove Columns apply to the full selection. Undo restores the cell selection after clearing, formatting, pasting, cutting rows, or removing table structure.
