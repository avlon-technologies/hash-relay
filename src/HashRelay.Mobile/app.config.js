// Expo app config. Firebase (push) is wired in only when the project's Firebase client files are present, so the
// app still builds and runs (subscriptions, publishing, foreground live delivery) before Firebase is set up.
// See README.md for how to obtain google-services.json / GoogleService-Info.plist.
const fs = require('fs');
const path = require('path');

// Locally the files sit next to this config (git-ignored). On EAS Build they arrive as secret file environment
// variables, whose values are paths to the materialised files: GOOGLE_SERVICES_JSON and GOOGLE_SERVICE_INFO_PLIST.
const local = (name) => (fs.existsSync(path.join(__dirname, name)) ? `./${name}` : null);
const androidFirebaseFile = process.env.GOOGLE_SERVICES_JSON || local('google-services.json');
const iosFirebaseFile = process.env.GOOGLE_SERVICE_INFO_PLIST || local('GoogleService-Info.plist');
const hasAndroidFirebase = !!androidFirebaseFile;
const hasIosFirebase = !!iosFirebaseFile;
// Decided per platform being built. @react-native-firebase/app's iOS plugin throws when GoogleService-Info.plist is
// missing, so an Android file alone must not switch Firebase on for an iOS build (and vice versa). EAS sets
// EAS_BUILD_PLATFORM; locally (expo start / run) either file is enough.
const buildPlatform = process.env.EAS_BUILD_PLATFORM;
const firebase = buildPlatform === 'ios' ? hasIosFirebase
  : buildPlatform === 'android' ? hasAndroidFirebase
  : hasAndroidFirebase || hasIosFirebase;

module.exports = ({ config }) => ({
  ...config,
  name: 'HashRelay',
  slug: 'hashrelay',
  owner: 'avlon-technologies-inc', // the Expo organisation that owns the EAS project and store builds
  version: '1.0.0',
  scheme: 'hashrelay',
  orientation: 'portrait',
  icon: './assets/icon.png',
  userInterfaceStyle: 'automatic',
  newArchEnabled: true,
  ios: {
    bundleIdentifier: 'ca.avlon.hashrelay',
    supportsTablet: false,
    ...(hasIosFirebase ? { googleServicesFile: iosFirebaseFile } : {}),
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
    ...(hasAndroidFirebase ? { googleServicesFile: androidFirebaseFile } : {}),
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
    // Read at run time per OS (src/config.ts): a dev build gets this from Metro, which does not know the target platform.
    firebase: { ios: hasIosFirebase, android: hasAndroidFirebase },
    eas: { projectId: process.env.EAS_PROJECT_ID || 'fd639ff4-b880-4458-88ff-5ef8bb5675d6' }, // EAS project under avlon-technologies-inc
  },
});
