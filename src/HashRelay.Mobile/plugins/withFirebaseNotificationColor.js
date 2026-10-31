// Config plugin: let the app's notification colour win over the one declared by @react-native-firebase/messaging.
// expo-notifications writes <meta-data com.google.firebase.messaging.default_notification_color> into the app
// manifest; the Firebase messaging library ships the same meta-data (@color/white) in its own manifest, and the
// manifest merger refuses the conflict unless the app marks its value as the replacement.
const { withAndroidManifest } = require('expo/config-plugins');

const META = 'com.google.firebase.messaging.default_notification_color';

module.exports = function withFirebaseNotificationColor(config) {
  return withAndroidManifest(config, (c) => {
    const manifest = c.modResults.manifest;
    manifest.$ = { ...manifest.$, 'xmlns:tools': 'http://schemas.android.com/tools' };
    for (const app of manifest.application ?? []) {
      for (const m of app['meta-data'] ?? []) {
        if (m.$['android:name'] === META) m.$['tools:replace'] = 'android:resource';
      }
    }
    return c;
  });
};
