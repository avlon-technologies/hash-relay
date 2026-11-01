// Expo app config. Firebase (push) is wired in only when the project's Firebase client files are present, so the
// app still builds and runs (subscriptions, publishing, foreground live delivery) before Firebase is set up.
// See README.md for how to obtain google-services.json / GoogleService-Info.plist.
const fs = require('fs');
const path = require('path');

const hasAndroidFirebase = fs.existsSync(path.join(__dirname, 'google-services.json'));
const hasIosFirebase = fs.existsSync(path.join(__dirname, 'GoogleService-Info.plist'));
const firebase = hasAndroidFirebase || hasIosFirebase;

module.exports = ({ config }) => ({
  ...config,
  name: 'HashRelay',
  slug: 'hashrelay',
  version: '1.0.0',
  scheme: 'hashrelay',
  orientation: 'portrait',
  icon: './assets/icon.png',
  userInterfaceStyle: 'automatic',
  newArchEnabled: true,
  ios: {
    bundleIdentifier: 'ca.avlon.hashrelay',
    supportsTablet: false,
    ...(hasIosFirebase ? { googleServicesFile: './GoogleService-Info.plist' } : {}),
    infoPlist: { UIBackgroundModes: ['remote-notification'] },
    entitlements: { 'aps-environment': 'production' },
  },
  android: {
    package: 'ca.avlon.hashrelay',
    adaptiveIcon: {
      backgroundColor: '#E6F4FE',
      foregroundImage: './assets/android-icon-foreground.png',
      backgroundImage: './assets/android-icon-background.png',
      monochromeImage: './assets/android-icon-monochrome.png',
    },
    ...(hasAndroidFirebase ? { googleServicesFile: './google-services.json' } : {}),
    permissions: ['android.permission.POST_NOTIFICATIONS'],
    predictiveBackGestureEnabled: false,
  },
  plugins: [
    // Mods run last-registered first, so this must precede expo-notifications to see the meta-data it writes.
    './plugins/withFirebaseNotificationColor',
    'expo-secure-store',
    ['expo-notifications', { color: '#2563eb' }],
    ['expo-build-properties', { ios: { useFrameworks: 'static' } }],
    ...(firebase ? ['@react-native-firebase/app', '@react-native-firebase/messaging'] : []),
  ],
  extra: {
    defaultServerUrl: process.env.HASHRELAY_SERVER_URL || 'https://hashrelay.dev.avlon.ca',
    firebase,
    ...(process.env.EAS_PROJECT_ID ? { eas: { projectId: process.env.EAS_PROJECT_ID } } : {}),
  },
});
