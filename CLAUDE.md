# Averse

Client torrent minimal (ajouter / télécharger, rien de plus : l'utilisateur ne veut pas de réglages en pagaille).
Tout le texte visible est en français. Repo `Ghermin/averse`, page GitHub dans `docs/` (https://ghermin.github.io/averse/).

- `ui/` : l'interface commune, embarquée dans l'exe (ressources) et dans l'APK (assets). Protocole : `{id, cmd, args}` → `{id, result|error}`, événements `{event: "toast"|"changed"}`. Ouverte dans un navigateur, elle tourne en démo.
- `windows/Averse` : .NET 8 WinForms + WebView2 + MonoTorrent 3.0.2. L'exe s'installe seul dans `%LOCALAPPDATA%\Programs\Averse` (`--portable` pour l'éviter), enregistre magnet: / .torrent dans HKCU et une entrée de désinstallation. Réglages `%APPDATA%\Averse\settings.json`, état du moteur `%LOCALAPPDATA%\Averse\cache`.
- MonoTorrent 3.0.2 n'amorce la DHT que via router.bittorrent.com (souvent muet) et relit `dht_nodes.cache` comme des nœuds bruts de 26 octets alors qu'il l'écrit en liste bencodée : `Engine.SeedDhtCacheAsync` réécrit ce fichier à chaque démarrage. Ne pas l'enlever, sinon les liens magnet sans tracker ne trouvent personne.
- `android` : Kotlin, libtorrent4j 2.1.0-39 (arm64, arm, x86_64). Reprise via fichiers `.resume` dans `filesDir/torrents`. Service de premier plan `specialUse` tant qu'un torrent travaille. Téléchargements directement dans `Download` (avant la 1.0.2 : `Download/Averse`, les anciens torrents gardent leur dossier).
- Signature APK : keystore hors du repo dans `%USERPROFILE%\.averse` (aussi en secrets GitHub `ANDROID_KEYSTORE_B64` / `ANDROID_KEYSTORE_PASSWORD`). Toujours le même, sinon il faut désinstaller pour mettre à jour.
- Appli par défaut : `DefaultApps.cs` écrit les clés UserChoice (ProgId + Hash signé, algorithme public vérifié contre les choix existants) pour magnet et .torrent. Forcé à la première installation (`--first-install`), puis seulement si le choix actuel est cassé (aucune appli / exe disparu). `Averse.exe --set-defaults` le force à la main. Sans ça, Chrome ne lance rien quand Windows n'a pas de choix magnet (cas après désinstallation de µTorrent).
- Mises à jour automatiques comme CamTel : `windows/Averse/Updater.cs` (au lancement puis toutes les 6 h, exe téléchargé dans %TEMP%\Averse-update puis lancé avec `--updated`, qui s'installe par-dessus) et `android/.../Updater.kt` (à l'ouverture, PackageInstaller). Pas de redémarrage pendant un téléchargement, sauf dans les 2 min après le lancement. Chaque version : `<Version>` du csproj et `versionName` par défaut de `android/app/build.gradle.kts`, étiquette `vX.Y.Z`, assets `Averse.exe` + `Averse.apk`. Le versionCode Android est dérivé de la version (1.2.3 → 10203).
- Tester l'appli Windows sans clics : lancer avec `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333` et piloter `host.call(...)` par CDP.

# Bored Dev Playground

Ce projet fait partie du Bored Dev Playground : https://ghermin.github.io/ (repo `Ghermin/Ghermin.github.io`, en local `../bored-dev-playground`).
Sa page GitHub (`docs/index.html`) et l'interface de l'appli (`ui/`) suivent le kit commun du playground.

Avant de modifier la page ou le style, se demander si le changement est local ou global :

- **Global** (bandeau, cartes, boutons, puces, pied de page, couleurs neutres, comportement clair / sombre…) : ne pas le faire ici. `docs/playground.css` et `ui/playground.css` sont des copies de `kit/playground.css` du playground : modifier le kit là-bas, lancer `sh kit/sync.sh`, puis commiter dans chaque projet.
- **Local** (couleurs ou police du projet) : le faire dans la page, en redéfinissant les variables `--pg-*` après le kit.
- Si le nom, la description, les tags, l'icône ou l'adresse du projet changent, mettre à jour sa carte dans `index.html` du playground.

À garder sur la page : le bandeau `pg-strip` qui ramène au playground, le thème clair / sombre automatique, le responsive (pas de défilement horizontal sur mobile), 2 polices max (celle du projet + JetBrains Mono). L'interface de l'appli n'a pas de bandeau.
