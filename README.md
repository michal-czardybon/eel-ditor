# Carbon Editor

Edytor zwykłego tekstu (C# / .NET 10 / Avalonia 11 / AvaloniaEdit) z:

- listą otwartych plików pogrupowaną według katalogów (zamiast zakładek), filtrem (Ctrl+Alt+F), przełączaniem MRU (Ctrl+Tab) i szybkim wyborem pliku (Ctrl+P),
- Markdownem edytowanym jako tekst: tabele są wyrównywane wirtualnie (źródło się nie zmienia), a znaczniki `**`, `*`, `~~`, `` ` ``, `#` i linki są ukryte; Ctrl+B / Ctrl+I i operacje na tabelach w menu Markdown,
- zachowaniem kodowania, BOM i znaków końca wiersza (zapis bajt w bajt), z ostrzeżeniem o mieszanych EOL,
- wykrywaniem zmian plików na dysku,
- wyszukiwaniem: Znajdź / Zamień / Znajdź w plikach (z tabelą wyników na dole okna),
- porównywaniem w dwóch kolumnach (z HEAD, z wersją na dysku, z innym plikiem), statusem git w liście plików i historią pliku.

## Uruchomienie

```
dotnet run --project src/CarbonPad.App -- [pliki...]
```

Program działa w jednym oknie: kolejne uruchomienie (np. dwuklik pliku w Eksploratorze) przekazuje pliki do otwartego okna. `--new-window` otwiera osobne okno.

## Instalator

```
.\build-installer.ps1
```

Buduje samodzielny `publish\win-x64\CarbonEditor.exe` i instalator Inno Setup 6 (`installer\CarbonEditor.iss`) w `artifacts\installer\`. Ikona programu powstaje skryptem `tools/make_icon.py`.

## Testy

```
dotnet test
```

## Struktura

| Projekt | Zawartość |
|---|---|
| `src/CarbonPad.Core` | kodowania, EOL, zapis atomowy, obserwowanie plików, wyszukiwanie, diff, git |
| `src/CarbonPad.Markdown` | parser Markdown, wyrównywanie tabel, operacje na tabelach (bez zależności od UI) |
| `src/CarbonPad.App` | aplikacja Avalonia: okno, menu, panel plików, rendering Markdown w edytorze, diff |
| `tests/*` | testy xUnit, w tym testy headless edytora |

Teksty interfejsu są w `src/CarbonPad.App/Lang/<kod>.json`: polski (`pl`), angielski (`en`) i międzysłowiański (`isv`, uwagi w `isv-notes.md`). Nowy język to nowy plik i wpis w `Loc.Languages`; test pilnuje, żeby klucze i `{0}` zgadzały się z polskim. Brakujące klucze biorą tekst z angielskiego, potem z polskiego.

Błędy, które nie zatrzymały programu, trafiają do `%APPDATA%/Carbon Editor/errors.log`.
