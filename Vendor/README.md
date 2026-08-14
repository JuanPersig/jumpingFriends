# Vendor

Dependencias de terceros demasiado grandes para versionar en git directo
(GitHub bloquea archivos individuales de más de 100MB sin Git LFS).

## MediaPipeUnityPlugin

`com.github.homuler.mediapipe-0.16.3.tgz` — referenciado desde
`Packages/manifest.json` con una ruta relativa
(`file:../Vendor/com.github.homuler.mediapipe-0.16.3.tgz`). **Si clonás
este repo en una máquina nueva, el proyecto no va a abrir hasta que pongas
ese archivo acá.**

Descargalo de: https://github.com/homuler/MediaPipeUnityPlugin/releases/tag/v0.16.3
(archivo `com.github.homuler.mediapipe-0.16.3.tgz` en los assets de la release).

Ver también `Assets/StreamingAssets/pose_landmarker_lite.bytes` (el modelo
de pose, ~5.8MB — ese sí está versionado, es chico).
