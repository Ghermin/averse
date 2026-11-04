# Averse

Le client torrent sans pub, pour **Windows** et **Android**. Colle un lien magnet ou ouvre un fichier .torrent :
c'est téléchargé. Pas de pub, pas de logiciel caché à l'installation, pas de mille réglages.

**Télécharger : https://ghermin.github.io/averse/**

- PC : [Averse.exe](https://github.com/Ghermin/averse/releases/latest/download/Averse.exe) (Windows 10 / 11, 64 bits). Il s'installe tout seul dans le menu Démarrer et devient l'appli par défaut des liens magnet et des fichiers .torrent (sans écraser plus tard un autre client choisi exprès).
- Téléphone : [Averse.apk](https://github.com/Ghermin/averse/releases/latest/download/Averse.apk) (Android 8 ou plus). Les téléchargements continuent écran éteint et vont dans `Téléchargements`.

## Ce que ça fait

Ajouter (lien magnet, empreinte, adresse d'un .torrent, fichier .torrent ouvert, glissé ou partagé), voir la progression,
le débit, le temps restant et les pairs, mettre en pause, reprendre, retirer (avec ou sans les fichiers), ouvrir le dossier.
Les torrents reprennent où ils en étaient au lancement suivant. Mises à jour automatiques depuis les versions GitHub (comme CamTel), jamais au milieu d'un téléchargement. Thème clair / sombre selon le système.

## Comment c'est fait

Une seule interface (HTML / CSS / JS dans `ui/`) pour les deux plateformes, avec le kit du
[Bored Dev Playground](https://ghermin.github.io/). Elle parle au moteur de l'appli par messages JSON.

| | Moteur | Fenêtre |
|---|---|---|
| `windows/Averse` | [MonoTorrent](https://github.com/alanmcgovern/monotorrent) (.NET 8) | WinForms + WebView2 |
| `android` | [libtorrent](https://libtorrent.org) via [libtorrent4j](https://github.com/aldenml/libtorrent4j) | WebView + service de premier plan |

Pour essayer l'interface seule dans un navigateur (mode démo, faux torrents) :

```bash
python -m http.server 5191 -d ui
```

Construire :

```bash
cd windows/Averse && dotnet publish -c Release -o ../../out
cd android && ./gradlew assembleRelease
```

Une étiquette `vX.Y.Z` poussée sur GitHub construit les deux et publie la version (`.github/workflows/build.yml`).
Les icônes (ICO, PNG, image de partage) viennent de `scripts/gen-icons.py`.

## Licence

MIT. Polices Outfit et JetBrains Mono sous licence SIL OFL. Averse ne fournit aucun contenu : respecte le droit d'auteur.
