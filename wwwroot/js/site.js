(function () {
  var musicAudio = null;
  var savePositionTimer = null;
  var pendingMusicStart = false;
  var MUSIC_SOURCE = "/media/relax.mp3";
  var MUSIC_MODE_KEY = "musicMode";
  var MUSIC_TIME_KEY = "musicTime";
  var MUSIC_VOLUME_KEY = "musicVolume";

  function normalizeVolume(value) {
    var parsed = Number(value);
    if (!Number.isFinite(parsed)) {
      return 0.35;
    }

    if (parsed > 1) {
      parsed = parsed / 100;
    }

    if (parsed < 0) {
      return 0;
    }

    if (parsed > 1) {
      return 1;
    }

    return parsed;
  }

  function getStoredVolume() {
    return normalizeVolume(localStorage.getItem(MUSIC_VOLUME_KEY) || 0.35);
  }

  function getOrCreateMusicElement() {
    if (musicAudio) {
      return musicAudio;
    }

    musicAudio = document.getElementById("global-music-player");
    if (!musicAudio) {
      musicAudio = document.createElement("audio");
      musicAudio.id = "global-music-player";
      musicAudio.preload = "auto";
      musicAudio.loop = true;
      musicAudio.style.display = "none";
      document.body.appendChild(musicAudio);
    }

    if (!musicAudio.getAttribute("src")) {
      musicAudio.setAttribute("src", MUSIC_SOURCE);
    }

    musicAudio.volume = getStoredVolume();

    return musicAudio;
  }

  function saveMusicTime() {
    if (!musicAudio || Number.isNaN(musicAudio.currentTime)) {
      return;
    }

    localStorage.setItem(MUSIC_TIME_KEY, String(musicAudio.currentTime));
  }

  function restoreMusicTime() {
    if (!musicAudio) {
      return;
    }

    var savedTime = parseFloat(localStorage.getItem(MUSIC_TIME_KEY) || "0");
    if (!Number.isFinite(savedTime) || savedTime <= 0) {
      return;
    }

    function applySavedTime() {
      try {
        musicAudio.currentTime = savedTime;
      } catch (e) {
        // metadata hazir degilse sessizce devam et
      }
    }

    if (musicAudio.readyState >= 1) {
      applySavedTime();
    } else {
      musicAudio.addEventListener("loadedmetadata", applySavedTime, { once: true });
    }
  }

  function startMusicPlayback() {
    var audio = getOrCreateMusicElement();
    restoreMusicTime();

    var playPromise = audio.play();
    if (playPromise && typeof playPromise.catch === "function") {
      playPromise.catch(function () {
        pendingMusicStart = true;
      });
    }

    if (!savePositionTimer) {
      savePositionTimer = setInterval(saveMusicTime, 1000);
    }
  }

  function stopMusicPlayback() {
    if (!musicAudio) {
      return;
    }

    saveMusicTime();
    musicAudio.pause();
    pendingMusicStart = false;

    if (savePositionTimer) {
      clearInterval(savePositionTimer);
      savePositionTimer = null;
    }
  }

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
    localStorage.setItem(MUSIC_MODE_KEY, mode);

    if (mode === 'on') {
      startMusicPlayback();
      return;
    }

    stopMusicPlayback();
  }

  function applyMusicVolume(value) {
    var volume = normalizeVolume(value);
    localStorage.setItem(MUSIC_VOLUME_KEY, String(volume));

    if (musicAudio) {
      musicAudio.volume = volume;
    }

    var slider = document.getElementById("musicVolumeRange");
    if (slider && document.activeElement !== slider) {
      slider.value = String(Math.round(volume * 100));
    }
  }

  function initMusicVolumeControl() {
    var slider = document.getElementById("musicVolumeRange");
    if (!slider) {
      return;
    }

    slider.value = String(Math.round(getStoredVolume() * 100));
  }

  var initialMusicMode = localStorage.getItem(MUSIC_MODE_KEY) || 'off';
  applyMusic(initialMusicMode === 'on' ? 'on' : 'off');

  window.setMusic = function (mode) {
    if (mode !== 'on' && mode !== 'off') {
      return;
    }

    applyMusic(mode);
  };

  window.setMusicVolume = function (value) {
    applyMusicVolume(value);
  };

  document.addEventListener("click", function () {
    if (!pendingMusicStart) {
      return;
    }

    if (localStorage.getItem(MUSIC_MODE_KEY) !== "on") {
      pendingMusicStart = false;
      return;
    }

    pendingMusicStart = false;
    startMusicPlayback();
  });

  window.addEventListener("beforeunload", saveMusicTime);

  applyMusicVolume(getStoredVolume());
  initMusicVolumeControl();
})();
