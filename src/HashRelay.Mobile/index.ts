import { registerRootComponent } from 'expo';
import App from './App';
import { configureForegroundDisplay, registerBackgroundPushHandler } from './src/push';

// Must run outside the component tree: the background handler is invoked in a headless JS context
// when a push arrives while the app is backgrounded or killed.
registerBackgroundPushHandler();
configureForegroundDisplay();

registerRootComponent(App);
