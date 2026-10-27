# AudioCut

Prosta aplikacja do wycinania fragmentów dźwięku z plików audio i wideo. Jeden plik `AudioCut.exe`, bez instalacji.

## Jak używać

1. Przeciągnij plik (MP4, MKV, MOV, MP3, WAV, …) na okno albo kliknij, żeby go wybrać.
2. Zaznacz fragment, przeciągając myszką po wykresie fali. Krawędzie zaznaczenia można chwytać i przesuwać.
3. Posłuchaj (Spacja), wybierz format i kliknij **Zapisz fragment**.

| Skrót | Działanie |
|---|---|
| Spacja | odtwórz / pauza |
| Home | odtwarzanie na początek zaznaczenia |
| I / O | początek / koniec zaznaczenia w miejscu odtwarzania |
| T | przytnij do zaznaczenia (reszta odcięta) |
| Delete | usuń zaznaczony fragment i sklej resztę |
| Ctrl+Z / Ctrl+Y | cofnij / ponów |
| ← / → (Shift) | przewiń o 1 s (5 s) |
| kółko myszy | zoom |
| Shift + kółko, Ctrl + przeciąganie | przewijanie widoku |
| Z / A | pokaż zaznaczenie / całość |
| Ctrl+A | zaznacz całość |
| Ctrl+O / Ctrl+S | otwórz / zapisz |

Czasy początku i końca można też wpisać ręcznie (np. `1:23.5` albo `83.5`).

Znacznik odtwarzania (biały uchwyt) przesuwa się klikając lub przeciągając po pasku czasu nad falą – nigdy nie zmienia wtedy zaznaczenia. Suwak **Głośność** (0–300%) działa na odsłuch i zapisany plik; przy przesterowaniu fala dostaje czerwone znaczniki, a zapis używa limitera.

Edycja jest nieniszcząca: plik źródłowy nigdy nie jest zmieniany, cięcia (pomarańczowe linie) są sklejane dopiero przy zapisie.

## Budowanie

Wymaga .NET 10 SDK.

```powershell
./build.ps1
```

Skrypt pobiera ffmpeg, pakuje go do exe i tworzy `dist\AudioCut.exe`. Przy pierwszym uruchomieniu ffmpeg jest rozpakowywany do `%LOCALAPPDATA%\AudioCut`.
