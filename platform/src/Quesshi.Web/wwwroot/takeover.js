// The invite page's hand-over to the WebAssembly app. The server renders #preview (with #join-button)
// and an empty #app; the app boots hidden into #app and calls claim() once it is ready.
(() => {
  const button = document.getElementById('join-button');
  const preview = document.getElementById('preview');
  const app = document.getElementById('app');
  if (!button || !preview || !app) return;

  let intent = false;
  app.hidden = true;

  // Before the app is ready, pressing join only records the intent and shows a spinner: nothing is sent.
  button.addEventListener('click', event => {
    event.preventDefault();
    if (intent) return;
    intent = true;
    button.setAttribute('aria-disabled', 'true');
    button.setAttribute('aria-busy', 'true');
    const spinner = document.createElement('div');
    spinner.id = 'join-spinner';
    spinner.className = 'spinner';
    spinner.setAttribute('role', 'status');
    button.after(spinner);
  });

  window.quesshi.takeover = {
    // The app is ready: the preview goes, the app shows, and the app learns whether join was pressed.
    claim: () => {
      preview.remove();
      app.hidden = false;
      return intent;
    }
  };
})();
