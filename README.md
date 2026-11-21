# Eel-ditor

Tekst + Markdown + Diff + Git

Czyli edytor zwykłego tekstu dla plików Markdown, który współpracuje z Gitem i nie zostawia śladów w diffie.

## Eal

*Eel* to po angielsku węgorz: żyje przy dnie, jest brzydki, ale ma specjalne umiejętności i zawsze przemyka niezauważony.

## Założenia projektowe

1. **Czysta historia w gicie przede wszystkim.**
   Każda zmiana w pliku to zmiana, którą użytkownik zrobił świadomie. Edytor niczego sam nie „poprawia”: nie
   wyrównuje tabel w źródle, nie przeformatowuje akapitów. Zapis odtwarza plik bajt w bajt (to samo kodowanie,
   BOM i EOL).
2. **Prosty widok tekstowy, w duchu ASCII.** Markdown jest edytowany jako tekst o stałej szerokości znaków. Znaczniki
   (`**`, `*`, `` ` ``, `#`, adresy linków) są ukryte, a tabele wyrównane wirtualnie: wyświetlane dopełnienie nie trafia
   do pliku. Przy zawijaniu wierszy tabela rysuje się jako siatka ASCII z tekstem zawiniętym w komórkach.
3. **Łatwe operowanie na tabelach.** Wstawianie i usuwanie wierszy i kolumn, przesuwanie, Tab między komórkami,
   edycja komórki w okienku. Każda operacja zmienia w źródle tylko to, co musi, i jest sprawdzana przed zapisem.
4. **Wiele plików otwartych jednocześnie, na liście zamiast zakładek.** Zakładki robią się niepraktyczne, gdy jest ich
   więcej niż kilkanaście; rozwiązujemy to za pomocą listy otwartych plików, pogrupowanej według katalogów, z filtrem,
   przełączaniem Ctrl+Tab (ostatnio używane) i szybkim wyborem (Ctrl+P) mieści dowolnie wiele plików.
5. **Diff w tej samej aplikacji.** Porównanie z HEAD, z wersją na dysku albo z innym plikiem, w dwóch kolumnach
   z kopiowaniem i cofaniem zmian albo jako różnice słów (`git diff --word-diff`), które pokazują, że po zmianie
   podziału na linie żadne słowo się nie zmieniło.
6. **Git pod ręką, bez zastępowania gita.** Status plików na liście, zakładka Git (stage, commit, push), historia
   pliku, rozwiązywanie konfliktów scalania. Edytor woła zwykły `git` z wiersza poleceń.
7. **Łamanie akapitów, które nie psuje diffów.** Nowe akapity mogą być łamane przy 80 lub 120 znakach albo po
   każdym zdaniu (semantic line breaks); istniejące akapity zostają nietknięte, chyba że użytkownik poprosi
   o przeformatowanie (Alt+Q).
8. **Szybki start i lekkość.** Jeden plik exe, ekran powitalny w ułamku sekundy, praca w tle (git, wyszukiwanie,
   diff) nigdy nie blokuje edytora. Bardzo duże pliki otwierają się bez kolorowania.
9. Skróty klawiszowe jak w Notepad++ i Visual Studio.
10. **Lokalnie i prywatnie.** Obrazki w Markdown są pokazywane tylko z plików lokalnych, nic nie jest pobierane
    z sieci. Ustawienia i sesja leżą w `%APPDATA%/Eel-ditor`.

## Co umie

- listę otwartych plików pogrupowaną według katalogów, z filtrem (Ctrl+Shift+P), przełączaniem MRU (Ctrl+Tab) i szybkim wyborem pliku (Ctrl+P),
- Markdown z ukrytymi znacznikami, wirtualnie wyrównanymi tabelami, listami zadań, przypisami, wyróżnieniem `==…==` i podglądem obrazków; Ctrl+B / Ctrl+I i operacje na tabelach w menu Markdown,
- zachowanie kodowania, BOM i znaków końca wiersza (zapis bajt w bajt), z ostrzeżeniem o mieszanych EOL,
- wykrywanie zmian plików na dysku,
- wyszukiwanie: Znajdź / Zamień / Znajdź w plikach (z tabelą wyników na dole okna),
- porównywanie w dwóch kolumnach lub jako różnice słów (z HEAD, z wersją na dysku, z innym plikiem), status git w liście plików i historię pliku,
- konwersję łacinka ↔ cyrylica (rosyjska, ukraińska, serbska, międzysłowiańska, polska).

## Uruchomienie

```
dotnet run --project src/EelDitor.App -- [pliki...]
```

Program działa w jednym oknie: kolejne uruchomienie (np. dwuklik pliku w Eksploratorze) przekazuje pliki do otwartego okna. `--new-window` otwiera osobne okno.

## Instalator

```
.\build-installer.ps1
```

Buduje samodzielny `publish\win-x64\EelDitor.exe` i instalator Inno Setup 6 (`installer\EelDitor.iss`) w `artifacts\installer\`. Ikona programu (węgorz) powstaje skryptem `tools/make_icon.py`.

## Testy

```
dotnet test
```

## Struktura

| Projekt | Zawartość |
|---|---|
| `src/EelDitor.Core` | kodowania, EOL, zapis atomowy, obserwowanie plików, wyszukiwanie, diff, git, łamanie akapitów, transliteracja |
| `src/EelDitor.Markdown` | parser Markdown, wyrównywanie tabel, operacje na tabelach (bez zależności od UI) |
| `src/EelDitor.App` | aplikacja Avalonia: okno, menu, panel plików, rendering Markdown w edytorze, diff |
| `tests/*` | testy xUnit, w tym testy headless edytora |

Teksty interfejsu są w `src/EelDitor.App/Lang/<kod>.json`: polski (`pl`), angielski (`en`) i międzysłowiański (`isv`, uwagi w `isv-notes.md`). Nowy język to nowy plik i wpis w `Loc.Languages`; test pilnuje, żeby klucze i `{0}` zgadzały się z polskim. Brakujące klucze biorą tekst z angielskiego, potem z polskiego.

Błędy, które nie zatrzymały programu, trafiają do `%APPDATA%/Eel-ditor/errors.log`.
