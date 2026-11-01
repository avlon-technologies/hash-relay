# HashRelay mobile (iOS + Android)

The first-party HashRelay app. Its job, in order:

1. **Register the phone** with a workspace once (device pairing, approved in the dashboard).
2. **Subscribe to hashtags** ahead of time; the phone receives matching notifications by push while the app is in the background and live over the realtime hub while it is open.
3. **Read** what was routed to it (inbox), and **publish** to hashtags when the device was approved with the publish permission.

Stack: Expo (React Native, TypeScript), `@react-native-firebase/messaging` for the push token on both platforms, `@microsoft/signalr` for foreground delivery, `expo-notifications` for local display and the Android channel. Push is Firebase Cloud Messaging only: Android directly, iOS relayed by Firebase to APNs with the APNs key uploaded to the Firebase project. HashRelay itself never talks to APNs.

## How it talks to the server

| Step | Call |
|---|---|
| Pair | `POST /api/v1/device/code` with `client: "mobile"` → show the code, open `/pair?code=…` → poll `POST /api/v1/device/token` |
| Register / token rotation | `PUT /api/v1/endpoints/{endpointId}` with `pushProvider: "fcm"` and `pushToken`; also sent in the hub `Hello` on every connect |
| Subscriptions | `GET/POST/DELETE /api/v1/endpoints/{endpointId}/subscriptions` |
| Publish | `POST /api/v1/notifications` (needs the `publish` scope on the device token) |
| Inbox | `GET /api/v1/endpoints/{endpointId}/deliveries` |
| Acknowledge | hub `Ack` when connected, otherwise `POST /api/v1/endpoints/{endpointId}/deliveries/{deliveryId}/ack` (used by the background push handler and when a push is opened) |

Delivery evidence: a push accepted by Firebase is recorded on the server as `pushed`, never as displayed. The app acknowledges when it actually handled the notification (background handler on Android, tap-to-open on either platform, live receipt in the foreground), which moves the delivery to `acked`.

The app disconnects from the hub when it goes to the background so the server routes to push instead of a stale socket.

## Running it

Prerequisites on this machine (installed under the user profile, no admin): Microsoft OpenJDK 17 at `%LOCALAPPDATA%\Programs\jdk-17`, the Android SDK at `%LOCALAPPDATA%\Android\Sdk` with platform-tools, emulator, platform 35, build-tools 35 and the `google_apis` x86_64 system image, and an AVD named `hashrelay` (Pixel 7). Hardware acceleration (WHPX) is verified working.

```powershell
$env:JAVA_HOME = "$env:LOCALAPPDATA\Programs\jdk-17"
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:PATH = "$env:JAVA_HOME\bin;$env:ANDROID_HOME\platform-tools;$env:ANDROID_HOME\emulator;$env:PATH"

cd src\HashRelay.Mobile
npm install
emulator -avd hashrelay            # or start it from Android Studio's device manager
npm run android                    # expo prebuild + gradle build + install on the running emulator
```

`npm start` afterwards serves the JS bundle with hot reload to the installed dev build. `npm run typecheck` runs `tsc`.

**Expo Go cannot be used**: the Firebase native module is not in Expo Go. Use the dev build above (Android) or an EAS development build (iOS).

### iOS

The iOS Simulator only exists in Xcode on macOS, so on a Windows machine the path is a cloud build on a physical iPhone:

```powershell
npm install -g eas-cli
eas login
eas device:create                                  # once: open the link on the iPhone to register it
eas build --profile development --platform ios     # dev client for registered iPhones (internal distribution)
npx expo start                                     # then open the dev client; JS reloads without rebuilding
```

EAS needs an Apple Developer Program membership. `eas build` walks you through the signing certificate and the provisioning profile, and can create the push key. Rebuild after adding a device or changing native code; plain JS changes only need `expo start`.

- `--profile preview`: an installable build without the dev menu, for registered iPhones only.
- TestFlight and the App Store: `eas build --profile production --platform ios`, then `eas submit -p ios`.

iOS push needs `GoogleService-Info.plist` (see below). Without it the iOS build leaves Firebase out and the app runs without background push. The Android file alone does not turn Firebase on for iOS.

## Firebase setup (needed for background push)

1. Create a Firebase project (console.firebase.google.com) and enable the **Firebase Cloud Messaging API (V1)**.
2. Add an **Android app** with package `ca.avlon.hashrelay`; download `google-services.json` into this folder.
3. Add an **iOS app** with bundle id `ca.avlon.hashrelay`; download `GoogleService-Info.plist` into this folder. Under *Project settings → Cloud Messaging → Apple app configuration*, upload the **APNs authentication key** (.p8, from the Apple Developer portal) with its key id and team id. This is what lets Firebase reach iPhones; HashRelay needs nothing else for iOS.
4. Under *Project settings → Service accounts*, generate a **private key** (JSON). Give it to the server as the configuration value `HashRelay:Push:Fcm:ServiceAccountJson` (env `HashRelay__Push__Fcm__ServiceAccountJson`, or `…:ServiceAccountPath` pointing at the file). In Azure this is the Terraform variable `fcm_service_account_json` (a Container App secret). Until it is set the server logs nothing about FCM and leaves push-capable devices queued for replay, so the app still works in the foreground.

Both client files are git-ignored. `app.config.js` only enables the Firebase config plugins when they exist, so a build without them is a valid "no push" build (the Settings screen says so).

The Android notification channel id is `hashrelay`; it must match `HashRelay:Push:Fcm:AndroidChannelId` on the server (default `hashrelay`).

## Layout

```
App.tsx                  root: pairing gate + hand-rolled tab bar
index.ts                 registers the background push handler before the app mounts
app.config.js            Expo config (conditional Firebase plugins)
src/config.ts            identity (endpoint id, device name) and pairing in SecureStore
src/api.ts               typed /api/v1 client
src/push.ts              FCM token, permission, background/foreground/open handlers, local display
src/realtime.ts          SignalR hub connection with Hello / Ack
src/store.tsx            app state and the pairing, subscription, publish and inbox actions
src/tags.ts              hashtag normalisation (same rules as HashRelay.Contracts.Tags)
src/ui.tsx               theme + primitives
src/screens/*.tsx        Pair, Subscriptions (home), Inbox, Publish, Settings
```
