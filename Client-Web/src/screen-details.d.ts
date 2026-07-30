// La Window Management API (getScreenDetails) todavía no está en los tipos DOM
// estándar de TypeScript — declaración mínima con lo que usa este proyecto.
// https://developer.chrome.com/docs/capabilities/web-apis/window-management

interface ScreenDetailed extends Screen {
  left: number;
  top: number;
  isPrimary: boolean;
}

interface ScreenDetails extends EventTarget {
  screens: ScreenDetailed[];
  currentScreen: ScreenDetailed;
}

interface Window {
  getScreenDetails?: () => Promise<ScreenDetails>;
}
