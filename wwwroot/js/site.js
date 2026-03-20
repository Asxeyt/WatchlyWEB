(function () {
  var musicAudio = null;
  var savePositionTimer = null;
  var pendingMusicStart = false;
  var MUSIC_SOURCE = "/media/relax.mp3";
  var MUSIC_MODE_KEY = "musicMode";
  var MUSIC_TIME_KEY = "musicTime";
  var MUSIC_VOLUME_KEY = "musicVolume";
  var PROFILE_KEY = "userProfile";

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

    var sliders = document.querySelectorAll("#musicVolumeRange, [data-music-volume]");
    sliders.forEach(function (slider) {
      if (document.activeElement !== slider) {
        slider.value = String(Math.round(volume * 100));
      }
    });
  }

  function initMusicVolumeControl() {
    var sliders = document.querySelectorAll("#musicVolumeRange, [data-music-volume]");
    if (!sliders.length) return;

    sliders.forEach(function (slider) {
      slider.value = String(Math.round(getStoredVolume() * 100));
      slider.addEventListener("input", function () {
        applyMusicVolume(slider.value);
      });
    });
  }

  function setLanguage(lang) {
    var nextLang = lang === "en" ? "en" : "tr";
    var url = new URL(window.location.href);
    url.searchParams.set("lang", nextLang);
    window.location.href = url.toString();
  }

  function getProfile() {
    try {
      var parsed = JSON.parse(localStorage.getItem(PROFILE_KEY) || "null");
      if (!parsed || !parsed.name || !parsed.handle) return null;
      return parsed;
    } catch (e) {
      return null;
    }
  }

  function getInitials(name) {
    if (!name) return "P";
    var parts = name.trim().split(/\s+/).slice(0, 2);
    var letters = parts.map(function (p) { return p.charAt(0).toUpperCase(); }).join("");
    return letters || "P";
  }

  function updateProfileAvatar(profile) {
    var avatars = document.querySelectorAll(".js-profile-avatar");
    avatars.forEach(function (avatar) {
      if (profile && profile.avatarUrl) {
        avatar.innerHTML = '<img alt="avatar" src="' + profile.avatarUrl + '" />';
      } else {
        avatar.textContent = getInitials(profile ? profile.name : "P");
      }
    });
  }

  function escapeHtml(value) {
    return String(value || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function renderProfileMenu() {
    var profile = getProfile();
    var menu = document.getElementById("profileMenuContent");
    var navText = document.getElementById("profileNavText");
    if (!menu || !navText) return;

    if (!profile) {
      navText.textContent = "Profil";
      updateProfileAvatar(null);
      menu.innerHTML =
        '<div class="profile-block">' +
        '<p class="mb-2 fw-bold">Kayıtlı profil yok</p>' +
        '<button type="button" class="btn btn-primary btn-sm" onclick="openProfileModal()">Kaydol</button>' +
        "</div>";
      return;
    }

    navText.textContent = profile.name;
    updateProfileAvatar(profile);
    menu.innerHTML =
      '<div class="profile-block border-bottom">' +
      '<p class="profile-name">' + escapeHtml(profile.name) + "</p>" +
      '<p class="profile-handle">@' + escapeHtml(profile.handle) + "</p>" +
      "</div>" +
      '<div class="profile-block d-grid gap-2">' +
      '<button type="button" class="btn btn-outline-secondary btn-sm" onclick="openProfileModal()">Profili duzenle</button>' +
      '<button type="button" class="btn btn-outline-danger btn-sm" onclick="clearProfile()">Oturumu kapat</button>' +
      "</div>";
  }

  function initProfileForm() {
    var form = document.getElementById("profileForm");
    if (!form) return;

    form.addEventListener("submit", function (e) {
      e.preventDefault();
      var nameInput = document.getElementById("profileName");
      var handleInput = document.getElementById("profileHandle");
      var avatarInput = document.getElementById("profileAvatar");
      if (!nameInput || !handleInput || !avatarInput) return;

      var profile = {
        name: nameInput.value.trim(),
        handle: handleInput.value.trim().replace(/^@+/, ""),
        avatarUrl: avatarInput.value.trim()
      };

      if (!profile.name || !profile.handle) return;

      localStorage.setItem(PROFILE_KEY, JSON.stringify(profile));
      renderProfileMenu();

      var modalElement = document.getElementById("profileModal");
      if (modalElement && window.bootstrap) {
        window.bootstrap.Modal.getOrCreateInstance(modalElement).hide();
      }
    });
  }

  function openProfileModal() {
    var profile = getProfile();
    var nameInput = document.getElementById("profileName");
    var handleInput = document.getElementById("profileHandle");
    var avatarInput = document.getElementById("profileAvatar");
    if (!nameInput || !handleInput || !avatarInput) return;

    nameInput.value = profile ? profile.name : "";
    handleInput.value = profile ? profile.handle : "";
    avatarInput.value = profile ? (profile.avatarUrl || "") : "";

    var modalElement = document.getElementById("profileModal");
    if (modalElement && window.bootstrap) {
      window.bootstrap.Modal.getOrCreateInstance(modalElement).show();
    }
  }

  function clearProfile() {
    localStorage.removeItem(PROFILE_KEY);
    renderProfileMenu();
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
  window.setLanguage = setLanguage;
  window.openProfileModal = openProfileModal;
  window.clearProfile = clearProfile;

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
  initProfileForm();
  renderProfileMenu();
})();
