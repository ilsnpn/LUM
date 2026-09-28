# LUM – bulle caméra pour démos vidéo

Affiche la webcam dans une bulle ronde, toujours au premier plan, et enregistre l'écran sur lequel se trouve la bulle (MP4 avec le son du micro).

**Télécharger :** [`LUM.exe` dans les Releases](https://github.com/ilsnpn/LUM/releases/latest) (un seul fichier, sans installation).
Logiciel libre sous [licence MIT](LICENSE).

## Utilisation
- Double-cliquer sur `LUM.exe`. Rien à installer (Windows 10/11, .NET Framework 4.8 déjà présent).
- Survoler la bulle pour afficher la barre : **● enregistrer**, **taille P / M / G**, **miroir**, **fermer (✕)**.
- Glisser la bulle n'importe où. Elle s'aimante aux bords et ne peut pas sortir de l'écran (multi-écran OK).
- Clic droit : taille, **choix de la caméra**, miroir, enregistrer, **choix du micro**, dossier des vidéos, fermer.

## Enregistrer (V2)
1. Placer la bulle sur l'écran à enregistrer, puis cliquer sur **●**. Un compte à rebours 3-2-1 démarre (cliquer sur ■ pour annuler).
2. Pendant l'enregistrement, la barre affiche **■ 0:12 | ❚❚ | 🗑** :
   - ■ : arrêter et enregistrer. La vidéo s'ouvre dans l'Explorateur.
   - ❚❚ : mettre en pause (le chrono clignote). Le ● rouge reprend l'enregistrement.
   - 🗑 : supprimer l'enregistrement (avec confirmation).
3. Les vidéos sont enregistrées dans `Vidéos\LUM\LUM_AAAA-MM-JJ_HH-MM-SS.mp4` (H.264 + AAC).

Détails :
- **Plusieurs écrans** : l'écran enregistré est celui où se trouve la bulle au moment du clic. Pendant l'enregistrement, la bulle ne peut pas quitter cet écran.
- **La bulle apparaît dans la vidéo, mais pas la barre de contrôle** (Windows 10 2004 ou plus récent). Sur un Windows plus ancien, la barre n'apparaît qu'au survol et sera visible dans la vidéo.
  Pendant un enregistrement, la bulle est aussi invisible pour un partage d'écran Teams.
- Le curseur de la souris est enregistré. Les écrans plus grands que la 4K sont réduits à la 4K.
- Son : micro par défaut de Windows, ou celui choisi dans le menu clic droit. S'il n'y a aucun micro, la vidéo est enregistrée sans son.
- Fermer LUM pendant un enregistrement termine et sauvegarde d'abord la vidéo.
- Si une vidéo sort à l'envers (rare, dépend du pilote) : mettre `recordflip=1` dans `%APPDATA%\LUM\settings.ini`.
- Taille, position, miroir et caméra sont mémorisés dans `%APPDATA%\LUM\settings.ini`.

## Recompiler
- **Sans rien installer** : lancer `build.bat` (utilise le `csc.exe` fourni avec Windows).
- **VS Code / .NET SDK** : `dotnet build` (projet `LUM.csproj`, cible net48).
- Le code reste en C# 5 (`LangVersion 5`) pour que `build.bat` continue de fonctionner.

## Structure
| Fichier | Rôle |
|---|---|
| `src/Program.cs` | Point d'entrée, une seule instance à la fois |
| `src/BubbleForm.cs` | Fenêtre ronde (layered), barre au survol, aimantation, rendu |
| `src/CameraCapture.cs` | Capture webcam via Media Foundation (API Windows native) |
| `src/ScreenRecorder.cs` | Enregistrement écran (capture GDI + encodeur MP4 + micro waveIn) |
| `src/MediaFoundation.cs` | Interop Media Foundation (lecture caméra, écriture MP4) |
| `src/Settings.cs` | Réglages mémorisés |
| `site/` | Site vitrine one page (`index.html`), l'exe à télécharger est dans `site/download/` (recopié par `build.bat`) |

## Si la caméra ne s'affiche pas
- Paramètres Windows > Confidentialité > Caméra : autoriser **les applications de bureau** à accéder à la caméra.
- La caméra est peut-être déjà utilisée par Teams, Zoom ou une autre application.
- Windows « N » : installer le Media Feature Pack.
- Un exe non signé peut être bloqué par l'IT (SmartScreen, AppLocker). Dans ce cas : signature de code ou liste blanche.

## Pistes pour la suite
- Raccourcis clavier globaux (démarrer / arrêter / pause).
- Enregistrer aussi le son de l'ordinateur (en plus du micro).
- Choisir une zone de l'écran ou une fenêtre au lieu de l'écran entier.
- Signer l'exe pour éviter les blocages SmartScreen / AppLocker.
