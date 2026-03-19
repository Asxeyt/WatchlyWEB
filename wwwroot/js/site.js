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
})();
