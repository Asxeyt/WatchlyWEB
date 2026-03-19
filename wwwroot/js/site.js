(function () {
  function applyTheme(theme) {
    document.documentElement.setAttribute('data-theme', theme);
    localStorage.setItem('theme', theme);
  }

  var initialTheme = localStorage.getItem('theme') || 'light';
  applyTheme(initialTheme);

  window.setTheme = function (theme) {
    if (theme !== 'dark' && theme !== 'light') {
      return;
    }

    applyTheme(theme);
  };

  function applyMusic(mode) {
    localStorage.setItem('musicMode', mode);
  }

  var initialMusicMode = localStorage.getItem('musicMode') || 'off';
  applyMusic(initialMusicMode === 'on' ? 'on' : 'off');

  window.setMusic = function (mode) {
    if (mode !== 'on' && mode !== 'off') {
      return;
    }

    applyMusic(mode);
  };
})();
