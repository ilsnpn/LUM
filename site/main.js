// LUM · site vitrine : bulle de démonstration, caméra, onglets, en-tête, dialogue de téléchargement.
(function () {
  'use strict';

  var $ = function (s, r) { return (r || document).querySelector(s); };
  var $$ = function (s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); };
  var reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  // Chemin d'une vidéo, comme la nomme l'app : Videos\LUM\LUM_AAAA-MM-JJ_HH-MM-SS.mp4
  function videoPath() {
    var d = new Date();
    var p = function (n) { return ('0' + n).slice(-2); };
    return 'C:\\Users\\[USER]\\Videos\\LUM\\LUM_' + d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) +
      '_' + p(d.getHours()) + '-' + p(d.getMinutes()) + '-' + p(d.getSeconds()) + '.mp4';
  }
  function fmtTime(ms) {
    var t = Math.floor(ms / 1000);
    return Math.floor(t / 60) + ':' + ('0' + t % 60).slice(-2);
  }

  // ------------------------------------------------------------ en-tête : clair ou sombre selon la section dessous
  var header = $('[data-header]');
  var hero = $('.hero');
  var horizon = $('.horizon');
  var darkZones = $$('[data-dark]');
  var headerTicking = false;

  function updateHeader() {
    headerTicking = false;
    var probe = 36;
    var dark = false;
    for (var i = 0; i < darkZones.length; i++) {
      var r = darkZones[i].getBoundingClientRect();
      var bottom = r.bottom;
      if (darkZones[i] === hero && horizon) bottom -= horizon.getBoundingClientRect().height * 0.55;
      if (r.top <= probe && bottom > probe) { dark = true; break; }
    }
    header.classList.toggle('on-light', !dark);
    header.classList.toggle('is-scrolled', window.scrollY > 8);
  }
  if (header) {
    window.addEventListener('scroll', function () {
      if (!headerTicking) { headerTicking = true; requestAnimationFrame(updateHeader); }
    }, { passive: true });
    window.addEventListener('resize', updateHeader);
    updateHeader();
  }

  // ------------------------------------------------------------ apparitions au défilement
  var reveals = $$('.reveal');
  if ('IntersectionObserver' in window && !reduceMotion) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); }
      });
    }, { threshold: 0.12, rootMargin: '0px 0px -40px 0px' });
    reveals.forEach(function (el) { io.observe(el); });
  } else {
    reveals.forEach(function (el) { el.classList.add('in'); });
  }

  // ------------------------------------------------------------ bulle de démonstration
  // Reproduit le comportement de l'application : 3 tailles, miroir, fermer,
  // aimantation à 28 px des bords, barre dessous (ou dessus si pas la place).
  var desk = $('[data-desk]');
  var bubble = $('[data-bubble]');
  if (desk && bubble) initBubble();

  function initBubble() {
    var face = $('[data-face]', bubble);
    var pill = $('[data-pill]', bubble);
    var relaunch = $('[data-relaunch]');
    var sizeBtns = $$('[data-size]', pill);
    var mirrorBtn = $('[data-mirror]', pill);
    var closeBtn = $('[data-close]', pill);
    var PILL_H = 44, OVERLAP = 16;

    var sizeIndex = 1, mirror = true;
    var fx = null, fy = null;          // centre de la bulle, en fraction du bureau
    var W = 0, H = 0, s = 0, x = 0, y = 0;
    var drag = null;

    function margin() { return Math.max(6, Math.round(W * 0.012)); }
    function magnet() { return Math.max(14, Math.round(W * 0.028)); }
    function sizeFor(i) { return Math.round([Math.max(64, H * 0.16), Math.max(84, H * 0.23), Math.max(112, H * 0.33)][i]); }

    function clamp(nx, ny, snap) {
      var m = margin(), mg = magnet();
      var minX = m, maxX = W - m - s, minY = m, maxY = H - m - s;
      if (snap) {
        if (nx - minX < mg) nx = minX;
        if (maxX - nx < mg) nx = maxX;
        if (ny - minY < mg) ny = minY;
        if (maxY - ny < mg) ny = maxY;
      }
      return [Math.max(minX, Math.min(maxX, nx)), Math.max(minY, Math.min(maxY, ny))];
    }

    function remember() { fx = (x + s / 2) / W; fy = (y + s / 2) / H; }

    function placePill() {
      var m = margin();
      var pw = pill.offsetWidth;
      var below = y + s - OVERLAP + PILL_H + m <= H;
      var py = below ? s - OVERLAP : -PILL_H + OVERLAP;
      var px = (s - pw) / 2;
      if (x + px < m) px += m - (x + px);
      if (x + px + pw > W - m) px -= (x + px + pw) - (W - m);
      px = Math.max(Math.min(0, s - pw), Math.min(Math.max(0, s - pw), px));
      pill.style.left = px + 'px';
      pill.style.top = py + 'px';
      bubble.classList.toggle('pill-above', !below);
    }

    function place() {
      bubble.style.setProperty('--s', s + 'px');
      bubble.style.left = x + 'px';
      bubble.style.top = y + 'px';
      bubble.style.right = 'auto';
      bubble.style.bottom = 'auto';
      placePill();
    }

    function measure(animate) {
      W = desk.clientWidth; H = desk.clientHeight;
      if (!W || !H) return;
      s = sizeFor(sizeIndex);
      if (fx === null) {
        var m = margin() + Math.round(W * 0.022);   // position de départ : en bas à droite, comme l'app
        x = W - m - s; y = H - m - s;
      } else {
        x = fx * W - s / 2; y = fy * H - s / 2;
      }
      var p = clamp(x, y, false); x = p[0]; y = p[1];
      remember();
      if (!animate) bubble.classList.add('dragging');
      place();
      if (!animate) requestAnimationFrame(function () { bubble.classList.remove('dragging'); });
    }

    function setSize(i) {
      if (i === sizeIndex) return;
      var cx = x + s / 2, cy = y + s / 2;
      sizeIndex = i;
      s = sizeFor(i);
      var p = clamp(cx - s / 2, cy - s / 2, false); x = p[0]; y = p[1];
      remember();
      place();
      sizeBtns.forEach(function (b) { b.setAttribute('aria-pressed', String(Number(b.getAttribute('data-size')) === i)); });
    }

    function touched() { bubble.classList.add('touched'); }

    sizeBtns.forEach(function (b) {
      b.addEventListener('click', function () { setSize(Number(b.getAttribute('data-size'))); touched(); });
    });
    mirrorBtn.addEventListener('click', function () {
      mirror = !mirror;
      bubble.classList.toggle('mirrored', mirror);
      mirrorBtn.setAttribute('aria-pressed', String(mirror));
      touched();
    });
    closeBtn.addEventListener('click', function () {
      bubble.classList.add('closing');
      setTimeout(function () {
        bubble.hidden = true;
        bubble.classList.remove('closing', 'show-pill');
        relaunch.hidden = false;
        relaunch.focus({ preventScroll: true });
      }, 200);
    });
    relaunch.addEventListener('click', function () {
      relaunch.hidden = true;
      bubble.hidden = false;
      measure(false);
      bubble.focus({ preventScroll: true });
    });

    // Enregistrement simulé : ● -> 3, 2, 1 -> ■ 0:00 | ❚❚ | 🗑, comme dans l'app
    var idleSet = $('[data-pill-idle]', pill), recSet = $('[data-pill-rec]', pill);
    var recBtn = $('[data-rec]', pill), stopBtn = $('[data-stop]', pill);
    var pauseBtn = $('[data-pause]', pill), discardBtn = $('[data-discard]', pill);
    var countEl = $('[data-count]', bubble), timerEl = $('[data-timer]', pill);
    var toast = $('[data-toast]'), toastTitle = $('[data-toast-title]'), toastPath = $('[data-toast-path]');
    var rec = { state: 'idle', elapsed: 0, last: 0, paused: false, timer: null, count: null };
    var toastTimer = null;

    function setRecState(state) {
      var hadFocus = pill.contains(document.activeElement);
      rec.state = state;
      bubble.classList.toggle('is-count', state === 'count');
      bubble.classList.toggle('is-rec', state === 'rec');
      bubble.classList.toggle('is-finishing', state === 'finishing');
      idleSet.hidden = state !== 'idle';
      recSet.hidden = state === 'idle';
      placePill();
      if (hadFocus) (state === 'idle' ? recBtn : stopBtn).focus({ preventScroll: true });
    }
    function showCount(n) {
      countEl.textContent = n;
      countEl.classList.remove('tick'); void countEl.offsetWidth; countEl.classList.add('tick');
    }
    function renderTimer() {
      var t = Math.floor(rec.elapsed / 1000);
      timerEl.textContent = Math.floor(t / 60) + ':' + ('0' + t % 60).slice(-2);
    }
    function setPaused(p) {
      var now = Date.now();
      if (p && !rec.paused && rec.state === 'rec') rec.elapsed += now - rec.last;
      rec.last = now;
      rec.paused = p;
      bubble.classList.toggle('is-paused', p);
      pauseBtn.setAttribute('aria-pressed', String(p));
      pauseBtn.setAttribute('aria-label', p ? 'Reprendre l’enregistrement' : 'Mettre en pause');
    }
    function showToast(title, path) {
      toastTitle.textContent = title;
      toastPath.textContent = path;
      toast.hidden = false;
      clearTimeout(toastTimer);
      toastTimer = setTimeout(function () { toast.hidden = true; }, 8000);
    }
    toast.addEventListener('click', function () { toast.hidden = true; });

    function startCountdown() {
      if (rec.state !== 'idle') return;
      touched();
      toast.hidden = true;
      timerEl.textContent = '0:00';
      setRecState('count');
      var n = 3;
      showCount(n);
      rec.count = setInterval(function () {
        n--;
        if (n > 0) { showCount(n); return; }
        clearInterval(rec.count);
        countEl.textContent = '';
        rec.elapsed = 0; rec.last = Date.now();
        setPaused(false);
        setRecState('rec');
        renderTimer();
        rec.timer = setInterval(function () {
          var now = Date.now();
          if (!rec.paused) rec.elapsed += now - rec.last;
          rec.last = now;
          renderTimer();
        }, 250);
      }, 1000);
    }
    function cancelCountdown() {
      clearInterval(rec.count);
      countEl.textContent = '';
      setRecState('idle');
    }
    function stopRecording(discard) {
      clearInterval(rec.timer);
      setPaused(false);
      if (discard) {
        setRecState('idle');
        showToast('Enregistrement supprimé', 'Aucune vidéo n’a été gardée.');
        return;
      }
      setRecState('finishing');
      countEl.textContent = 'Finalisation…';
      setTimeout(function () {
        countEl.textContent = '';
        setRecState('idle');
        showToast('Vidéo enregistrée', videoPath());
      }, 900);
    }

    recBtn.addEventListener('click', startCountdown);
    stopBtn.addEventListener('click', function () {
      if (rec.state === 'count') cancelCountdown();
      else if (rec.state === 'rec') stopRecording(false);
    });
    pauseBtn.addEventListener('click', function () { if (rec.state === 'rec') setPaused(!rec.paused); });
    discardBtn.addEventListener('click', function () {
      if (rec.state === 'count') { cancelCountdown(); return; }
      if (rec.state !== 'rec') return;
      var wasPaused = rec.paused;
      setPaused(true);
      if (window.confirm('Supprimer cet enregistrement ?')) stopRecording(true);
      else if (!wasPaused) setPaused(false);
    });

    // Glisser (souris, stylet, doigt)
    face.addEventListener('pointerdown', function (e) {
      if (e.button !== 0) return;
      e.preventDefault();
      face.setPointerCapture(e.pointerId);
      drag = { id: e.pointerId, sx: e.clientX, sy: e.clientY, ox: x, oy: y, moved: false, type: e.pointerType };
      bubble.classList.add('dragging');
      touched();
    });
    face.addEventListener('pointermove', function (e) {
      if (!drag || e.pointerId !== drag.id) return;
      var dx = e.clientX - drag.sx, dy = e.clientY - drag.sy;
      if (!drag.moved && Math.abs(dx) + Math.abs(dy) < 4) return;
      drag.moved = true;
      var p = clamp(drag.ox + dx, drag.oy + dy, true); x = p[0]; y = p[1];
      place();
    });
    function endDrag(e) {
      if (!drag || e.pointerId !== drag.id) return;
      var tap = !drag.moved, type = drag.type;
      drag = null;
      bubble.classList.remove('dragging');
      remember();
      if (tap && type !== 'mouse') bubble.classList.toggle('show-pill');
    }
    face.addEventListener('pointerup', endDrag);
    face.addEventListener('pointercancel', endDrag);

    // Sur écran tactile, toucher ailleurs masque la barre
    document.addEventListener('pointerdown', function (e) {
      if (!bubble.contains(e.target)) bubble.classList.remove('show-pill');
    });

    // Clavier : flèches pour déplacer (Maj pour aller plus vite)
    bubble.addEventListener('keydown', function (e) {
      if (e.target !== bubble) return;
      var step = e.shiftKey ? 48 : 12;
      var d = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }[e.key];
      if (!d) return;
      e.preventDefault();
      var p = clamp(x + d[0], y + d[1], true); x = p[0]; y = p[1];
      remember(); place(); touched();
    });

    // L'indication « glissez-moi » s'efface après un survol prolongé
    var hint = $('.bubble-hint', bubble);
    if (hint && window.matchMedia('(hover: none)').matches) hint.textContent = 'Touchez-moi, puis ●';
    var hintTimer = null;
    bubble.addEventListener('pointerenter', function () { hintTimer = setTimeout(touched, 1600); });
    bubble.addEventListener('pointerleave', function () { clearTimeout(hintTimer); });

    if ('ResizeObserver' in window) new ResizeObserver(function () { measure(false); }).observe(desk);
    else window.addEventListener('resize', function () { measure(false); });
    measure(false);
  }

  // ------------------------------------------------------------ démo multi-écran
  // Comme dans l'app : l'écran enregistré est celui de la bulle au moment du clic sur ●,
  // et la bulle ne peut plus le quitter pendant l'enregistrement.
  var ms = $('[data-ms]');
  if (ms) initMulti();

  function initMulti() {
    var mb = $('[data-ms-bubble]', ms);
    var monitors = $$('[data-monitor]', ms);
    var screens = monitors.map(function (m) { return $('.mon-screen', m); });
    var tags = monitors.map(function (m) { return $('.mon-tag', m); });
    var recBtn = $('[data-ms-rec]'), recLabel = $('[data-ms-rec-label]');
    var status = $('[data-ms-status]'), countEl = $('[data-ms-count]', mb);
    var cur = 0, locked = null, state = 'idle';
    var fx = 0.74, fy = 0.64;          // centre de la bulle, en fraction de l'écran courant
    var x = 0, y = 0, s = 0, drag = null;
    var elapsed = 0, last = 0, timer = null, countTimer = null;

    function rects() {
      var st = ms.getBoundingClientRect();
      return screens.map(function (sc) {
        var r = sc.getBoundingClientRect();
        return { l: r.left - st.left, t: r.top - st.top, w: r.width, h: r.height };
      });
    }
    function clampTo(i, nx, ny, snap) {
      var r = rects()[i];
      var m = Math.max(4, r.w * 0.025), mg = Math.max(8, r.w * 0.05);
      var minX = r.l + m, maxX = r.l + r.w - m - s, minY = r.t + m, maxY = r.t + r.h - m - s;
      if (snap) {
        if (nx - minX < mg) nx = minX;
        if (maxX - nx < mg) nx = maxX;
        if (ny - minY < mg) ny = minY;
        if (maxY - ny < mg) ny = maxY;
      }
      return [Math.max(minX, Math.min(maxX, nx)), Math.max(minY, Math.min(maxY, ny))];
    }
    function monitorAt(px) {
      var best = 0, bestD = Infinity;
      rects().forEach(function (r, i) {
        var d = px < r.l ? r.l - px : px > r.l + r.w ? px - (r.l + r.w) : 0;
        if (d < bestD) { bestD = d; best = i; }
      });
      return best;
    }
    function remember() { var r = rects()[cur]; fx = (x + s / 2 - r.l) / r.w; fy = (y + s / 2 - r.t) / r.h; }
    function place() {
      mb.style.setProperty('--s', s + 'px');
      mb.style.left = x + 'px'; mb.style.top = y + 'px';
      mb.style.right = 'auto'; mb.style.bottom = 'auto';
      paintScreens();
    }
    function layout() {
      var r = rects()[cur];
      s = Math.round(Math.max(40, r.w * 0.22));
      var p = clampTo(cur, r.l + fx * r.w - s / 2, r.t + fy * r.h - s / 2, false); x = p[0]; y = p[1];
      mb.classList.add('dragging');
      place();
      requestAnimationFrame(function () { mb.classList.remove('dragging'); });
    }
    function paintScreens() {
      monitors.forEach(function (m, i) {
        var on = i === cur;
        m.classList.toggle('is-target', on && state !== 'rec');
        m.classList.toggle('is-rec', on && state === 'rec');
        m.classList.toggle('is-off', !on && state === 'rec');
        tags[i].textContent = !on ? '' : state === 'rec' ? '● REC ' + fmtTime(elapsed) : state === 'count' ? 'Enregistrement dans un instant' : 'Écran de la bulle';
      });
    }
    function idleStatus() { status.innerHTML = 'La bulle est sur l’écran ' + (cur + 1) + '&nbsp;: c’est lui qui sera enregistré.'; }
    function warnLocked() {
      status.textContent = 'Pendant l’enregistrement, la bulle reste sur l’écran ' + (locked + 1) + '. L’autre écran n’est pas filmé.';
      mb.classList.remove('nudge'); void mb.offsetWidth; mb.classList.add('nudge');
    }
    function switchTo(i) {
      if (i === cur) return;
      cur = i;
      if (state === 'idle') idleStatus();
    }

    // Glisser : la bulle passe d'un écran à l'autre, sauf pendant l'enregistrement
    mb.addEventListener('pointerdown', function (e) {
      if (e.button !== 0) return;
      e.preventDefault();
      mb.setPointerCapture(e.pointerId);
      drag = { id: e.pointerId, sx: e.clientX, sy: e.clientY, ox: x, oy: y, warned: false };
      mb.classList.add('dragging');
    });
    mb.addEventListener('pointermove', function (e) {
      if (!drag || e.pointerId !== drag.id) return;
      var px = e.clientX - ms.getBoundingClientRect().left;
      var under = monitorAt(px);
      if (locked !== null) {
        if (under !== locked && !drag.warned) { drag.warned = true; warnLocked(); }
      } else switchTo(under);
      var p = clampTo(cur, drag.ox + e.clientX - drag.sx, drag.oy + e.clientY - drag.sy, true); x = p[0]; y = p[1];
      place();
    });
    function endDrag(e) {
      if (!drag || e.pointerId !== drag.id) return;
      drag = null;
      mb.classList.remove('dragging');
      remember();
    }
    mb.addEventListener('pointerup', endDrag);
    mb.addEventListener('pointercancel', endDrag);

    // Clavier : flèches ; au bord d'un écran, on passe au suivant
    mb.addEventListener('keydown', function (e) {
      var step = e.shiftKey ? 48 : 16;
      var d = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }[e.key];
      if (!d) return;
      e.preventDefault();
      var p = clampTo(cur, x + d[0], y + d[1], true);
      var next = cur + (d[0] > 0 ? 1 : -1);
      if (d[0] !== 0 && p[0] === x && next >= 0 && next < monitors.length) {
        if (locked !== null) { warnLocked(); return; }
        switchTo(next);
        var r = rects()[next];
        p = clampTo(next, d[0] > 0 ? r.l : r.l + r.w, y, true);
      }
      x = p[0]; y = p[1];
      place(); remember();
    });

    // Enregistrer : 3, 2, 1, puis écran verrouillé
    function reset() {
      clearInterval(countTimer); clearInterval(timer);
      state = 'idle'; locked = null;
      countEl.textContent = '';
      mb.classList.remove('is-count');
      recBtn.classList.remove('is-rec');
      recLabel.textContent = 'Enregistrer cet écran';
    }
    recBtn.addEventListener('click', function () {
      if (state === 'count') { reset(); idleStatus(); paintScreens(); return; }
      if (state === 'rec') {
        var n = cur + 1;
        reset(); paintScreens();
        status.innerHTML = 'Vidéo enregistrée&nbsp;: <code>' + videoPath() + '</code>. Seul l’écran ' + n + ' y figure.';
        return;
      }
      state = 'count'; locked = cur;
      mb.classList.add('is-count');
      recLabel.textContent = 'Annuler';
      status.textContent = 'Enregistrement de l’écran ' + (cur + 1) + ' dans 3 secondes…';
      paintScreens();
      var k = 3;
      countEl.textContent = k;
      countTimer = setInterval(function () {
        k--;
        if (k > 0) { countEl.textContent = k; return; }
        clearInterval(countTimer);
        countEl.textContent = '';
        mb.classList.remove('is-count');
        state = 'rec'; elapsed = 0; last = Date.now();
        recBtn.classList.add('is-rec');
        recLabel.textContent = 'Arrêter · 0:00';
        status.textContent = 'Enregistrement de l’écran ' + (cur + 1) + ' uniquement. Essayez de glisser la bulle vers l’autre écran.';
        paintScreens();
        timer = setInterval(function () {
          var now = Date.now(); elapsed += now - last; last = now;
          recLabel.textContent = 'Arrêter · ' + fmtTime(elapsed);
          paintScreens();
        }, 250);
      }, 1000);
    });

    if ('ResizeObserver' in window) new ResizeObserver(layout).observe(ms);
    else window.addEventListener('resize', layout);
    layout();
  }

  // Petit chrono qui tourne dans l'illustration « Enregistrez en un clic »
  var rdTimer = $('.rd-timer');
  if (rdTimer && !reduceMotion) {
    var rdT = 12;
    setInterval(function () { rdT = rdT >= 59 ? 12 : rdT + 1; rdTimer.textContent = '0:' + ('0' + rdT).slice(-2); }, 1000);
  }

  // ------------------------------------------------------------ caméra (facultatif, reste dans le navigateur)
  var camBtn = $('[data-cam]');
  var camLabel = $('[data-cam-label]');
  var camNote = $('[data-cam-note]');
  var defaultNote = camNote ? camNote.textContent : '';
  var stream = null;

  function setCamUi(on) {
    document.documentElement.classList.toggle('cam-on', on);
    camBtn.setAttribute('aria-pressed', String(on));
    camLabel.textContent = on ? 'Couper ma caméra' : 'Essayer avec ma caméra';
  }

  function camOff() {
    if (stream) stream.getTracks().forEach(function (t) { t.stop(); });
    stream = null;
    $$('video.cam').forEach(function (v) { v.srcObject = null; });
    setCamUi(false);
    camNote.textContent = defaultNote;
  }

  function camOn() {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      camNote.textContent = 'Votre navigateur ne donne pas accès à la caméra sur cette page. La démo fonctionne aussi sans.';
      return;
    }
    camBtn.disabled = true;
    camLabel.textContent = 'Autorisez la caméra…';
    navigator.mediaDevices.getUserMedia({
      video: { width: { ideal: 640 }, height: { ideal: 640 }, facingMode: 'user' },
      audio: false
    }).then(function (st) {
      stream = st;
      $$('video.cam').forEach(function (v) {
        v.srcObject = st;
        var p = v.play();
        if (p && p.catch) p.catch(function () {});
      });
      var track = st.getVideoTracks()[0];
      if (track) track.addEventListener('ended', camOff);
      setCamUi(true);
      camNote.textContent = 'Vous voilà en lumière. Votre image reste dans votre navigateur.';
    }).catch(function (err) {
      stream = null;
      setCamUi(false);
      var n = err && err.name;
      camNote.textContent =
        n === 'NotAllowedError' ? 'Accès à la caméra refusé. Pas de souci : la démo marche aussi sans.' :
        n === 'NotFoundError' || n === 'OverconstrainedError' ? 'Aucune caméra détectée sur cet appareil.' :
        n === 'NotReadableError' ? 'La caméra est déjà utilisée par une autre application.' :
        'Impossible d’accéder à la caméra ici. La démo fonctionne aussi sans.';
    }).then(function () { camBtn.disabled = false; });
  }

  if (camBtn) {
    camBtn.setAttribute('aria-pressed', 'false');
    camBtn.addEventListener('click', function () { if (stream) camOff(); else camOn(); });
  }

  // ------------------------------------------------------------ onglets des exemples
  var tabs = $$('[role="tab"]');
  function selectTab(tab, focus) {
    tabs.forEach(function (t) {
      var on = t === tab;
      t.setAttribute('aria-selected', String(on));
      t.tabIndex = on ? 0 : -1;
      var panel = document.getElementById(t.getAttribute('aria-controls'));
      if (panel) panel.hidden = !on;
    });
    if (focus) tab.focus();
  }
  tabs.forEach(function (tab, i) {
    tab.addEventListener('click', function () { selectTab(tab, false); });
    tab.addEventListener('keydown', function (e) {
      var j = null;
      if (e.key === 'ArrowRight') j = (i + 1) % tabs.length;
      else if (e.key === 'ArrowLeft') j = (i - 1 + tabs.length) % tabs.length;
      else if (e.key === 'Home') j = 0;
      else if (e.key === 'End') j = tabs.length - 1;
      if (j !== null) { e.preventDefault(); selectTab(tabs[j], true); }
    });
  });

  // ------------------------------------------------------------ après le téléchargement
  var dialog = $('[data-dialog]');
  if (dialog && typeof dialog.showModal === 'function') {
    $$('[data-download]').forEach(function (a) {
      a.addEventListener('click', function () {
        setTimeout(function () { if (!dialog.open) dialog.showModal(); }, 400);
      });
    });
    $('[data-dialog-close]', dialog).addEventListener('click', function () { dialog.close(); });
    dialog.addEventListener('click', function (e) { if (e.target === dialog) dialog.close(); });
  }

  // ------------------------------------------------------------ hors Windows : prévenir
  if (!/Windows/i.test(navigator.userAgent)) {
    var note = $('[data-platform-note]');
    if (note) note.hidden = false;
  }
  var copyBtn = $('[data-copy-link]');
  if (copyBtn) {
    copyBtn.addEventListener('click', function () {
      var url = location.href.split('#')[0];
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(url).then(function () { copyBtn.textContent = 'lien copié'; },
          function () { copyBtn.textContent = 'copiez l’adresse de cette page'; });
      } else {
        copyBtn.textContent = 'copiez l’adresse de cette page';
      }
    });
  }
})();
