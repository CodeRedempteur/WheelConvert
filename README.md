# WheelConvert 3.0

Roue radiale pour Windows : conversions de fichiers et actions rapides sans quitter l'Explorateur.

## Utilisation
| Geste | Résultat |
|---|---|
| **Clic gauche + glisser, puis Shift** | Roue de conversion adaptée au type (image, vidéo, audio, 3D) — déposer sur un format |
| **Shift + clic droit maintenu** (partout) | Roues d'actions — **molette** pour changer de roue, relâcher sur une action |
| **Ctrl + Alt + Espace** | Roues d'actions au curseur — cliquer · molette / ←→ / 1-9 pour changer de roue · Échap |
| Clic sur l'icône de la barre des tâches | Designer (roues + paramètres) |

Un secteur « Ouvrir une autre roue » bascule de roue au survol (ou au clic) sans fermer.

## Stream Deck
Les roues créées dans Stream Deck Script Builder (onglet « Roues ») arrivent en direct après les tiennes
(`%AppData%\WheelConvert\providers\streamdeck.json`).

## Conversions
- **Images** → PNG · JPG · WEBP · ICO multi-tailles · PDF (fusion) · 50 %
- **Vidéos** → MP4 · MP3 · GIF · WEBM · 720p · miniature *(FFmpeg)*
- **Audio** → MP3 · WAV · FLAC · M4A · OGG *(FFmpeg)*
- **3D** (STL, OBJ, 3MF, PLY, FBX, GLB, GLTF, DAE, 3DS) → STL · 3MF · OBJ · PLY
- **Tout** → ZIP · copier les chemins · SHA-256

## FFmpeg
Requis pour la vidéo et l'audio : `winget install Gyan.FFmpeg` (bouton dans le designer, onglet Général),
ou place `ffmpeg.exe` à côté de `WheelConvert.exe`.

## Paramètres
`%AppData%\WheelConvert\settings.json`
