(function () {
  var audioContext = null;
  var masterGain = null;
  var lfo = null;
  var lfoGain = null;
  var baseOsc1 = null;
  var baseOsc2 = null;
  var chordTimer = null;

  var relaxingNotes = [196.0, 220.0, 246.94, 261.63, 293.66];
  var noteIndex = 0;

  function nextRelaxingNote() {
    var note = relaxingNotes[noteIndex % relaxingNotes.length];
    noteIndex += 1;
    return note;
  }

  function setChord(note) {
    if (!baseOsc1 || !baseOsc2 || !audioContext) {
      return;
    }

    var now = audioContext.currentTime;
    baseOsc1.frequency.cancelScheduledValues(now);
    baseOsc2.frequency.cancelScheduledValues(now);
    baseOsc1.frequency.linearRampToValueAtTime(note, now + 1.8);
    baseOsc2.frequency.linearRampToValueAtTime(note * 1.5, now + 1.8);
  }

  function stopRelaxingMusic() {
    if (chordTimer) {
      clearInterval(chordTimer);
      chordTimer = null;
    }

    if (baseOsc1) {
      baseOsc1.stop();
      baseOsc1.disconnect();
      baseOsc1 = null;
    }

    if (baseOsc2) {
      baseOsc2.stop();
      baseOsc2.disconnect();
      baseOsc2 = null;
    }

    if (lfo) {
      lfo.stop();
      lfo.disconnect();
      lfo = null;
    }

    if (lfoGain) {
      lfoGain.disconnect();
      lfoGain = null;
    }

    if (masterGain) {
      masterGain.disconnect();
      masterGain = null;
    }

    if (audioContext) {
      audioContext.close();
      audioContext = null;
    }
  }

  function startRelaxingMusic() {
    if (audioContext) {
      return;
    }

    var AudioCtx = window.AudioContext || window.webkitAudioContext;
    if (!AudioCtx) {
      return;
    }

    audioContext = new AudioCtx();
    masterGain = audioContext.createGain();
    masterGain.gain.value = 0.03;
    masterGain.connect(audioContext.destination);

    baseOsc1 = audioContext.createOscillator();
    baseOsc2 = audioContext.createOscillator();
    baseOsc1.type = "sine";
    baseOsc2.type = "triangle";

    var osc1Gain = audioContext.createGain();
    var osc2Gain = audioContext.createGain();
    osc1Gain.gain.value = 0.8;
    osc2Gain.gain.value = 0.45;

    baseOsc1.connect(osc1Gain);
    baseOsc2.connect(osc2Gain);
    osc1Gain.connect(masterGain);
    osc2Gain.connect(masterGain);

    lfo = audioContext.createOscillator();
    lfoGain = audioContext.createGain();
    lfo.type = "sine";
    lfo.frequency.value = 0.08;
    lfoGain.gain.value = 3.5;
    lfo.connect(lfoGain);
    lfoGain.connect(baseOsc1.detune);

    setChord(nextRelaxingNote());
    baseOsc1.start();
    baseOsc2.start();
    lfo.start();

    chordTimer = setInterval(function () {
      setChord(nextRelaxingNote());
    }, 9000);
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
    localStorage.setItem('musicMode', mode);

    if (mode === 'on') {
      startRelaxingMusic();
      return;
    }

    stopRelaxingMusic();
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
