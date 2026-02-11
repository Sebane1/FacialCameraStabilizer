This project is fairly simple.
It proxies camera feeds from multiple ESP32 based camera's, and allows creating multiple endpoints to those cameras with support for multiple listeners.
Additionally adds the ability to auto reconnect to temporarily disconnected camera's without interruption to software that listens to the proxied camera feed.

## Data flow options

You can choose which side acts as server for the camera data between **FacialCameraBroadcaster** (e.g. on Quest/Android) and **FacialCameraStabilizer** (proxies and re-hosts on localhost).

- **Server mode (default)**  
  Broadcaster hosts the MJPEG streams (e.g. `http://device-ip:8080/`). Stabilizer pulls from those URLs (set `Url` in `config.json`) and re-hosts on localhost.

- **Client mode**  
  Stabilizer listens for pushed frames on configurable ports. Broadcaster connects to Stabilizer’s host and pushes frames. Use when it’s easier for the PC running Stabilizer to accept incoming connections (e.g. fixed IP) than for the Quest to be reached.

### Stabilizer `config.json` (listen-for-push)

To have Stabilizer listen for Broadcaster (client mode), use `IngestPort` instead of `Url` for a camera:

```json
[
  {
    "Name": "Left Eye",
    "IngestPort": 9080,
    "Port": 8080,
    "CameraPathAliases": ["left", "eye"]
  }
]
```

Default ingest ports used by Broadcaster in client mode: Left 9080, Right 9081, Mouth 9082. In Broadcaster, choose “Client – push to Stabilizer” and set the Stabilizer host (IP or hostname).
