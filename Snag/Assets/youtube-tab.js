// Snag's script for the YouTube tab (Views\BrowserView.xaml.cs adds it). It runs in every YouTube page
// before the page's own scripts, and does four things:
//   1. Ad blocking (when switched on): strips the ad data out of YouTube's player responses so no ads are
//      scheduled, hides ad boxes in the feed and on watch pages, and skips any ad that still gets through.
//   2. "Play clicked videos in Snag": with that on, clicking a video hands it to Snag's player instead.
//   3. Tells Snag when a video starts playing on the page, so Snag's own player pauses (and vice versa).
//   4. Skips sponsor segments (SponsorBlock) and shows the dislike count (Return YouTube Dislike), using what
//      Snag looked up and sent over.
// It only ever talks to Snag through window.chrome.webview, with a few fixed message types.
(function (adBlock) {
  'use strict';
  // On every page in the tab (Google's sign-in too): tell Snag when the page gets keyboard focus. Snag's window
  // isn't told otherwise, and would give typing back to its own search box whenever it's switched back to.
  if (window.top === window && window.chrome && window.chrome.webview) {
    window.addEventListener('focus', (event) => {
      if (event.target !== window) return;
      try { window.chrome.webview.postMessage({ type: 'pagefocus' }); } catch (_) { /* Snag went away */ }
    });
  }

  if (!/(^|\.)youtube\.com$/.test(location.hostname)) return;

  // A slim scrollbar without arrow buttons, like the rest of Snag.
  const addLook = () => {
    if (document.getElementById('snag-look') || !document.documentElement) return;
    const style = document.createElement('style');
    style.id = 'snag-look';
    style.textContent = 'html { scrollbar-width: thin; scrollbar-color: rgba(128, 128, 128, 0.55) transparent; }';
    (document.head || document.documentElement).appendChild(style);
  };
  addLook();
  document.addEventListener('DOMContentLoaded', addLook);

  const webview = window.chrome && window.chrome.webview;
  const isTop = window.top === window;
  const post = (message) => {
    if (!isTop || !webview) return;
    try { webview.postMessage(message); } catch (_) { /* Snag went away */ }
  };

  // ------------------------------------------------------------------ 1. ad blocking
  if (adBlock) {
    const AD_KEYS = ['adPlacements', 'adSlots', 'playerAds', 'adBreakHeartbeatParams'];
    const strip = (o) => {
      if (!o || typeof o !== 'object') return false;
      let hit = false;
      for (const key of AD_KEYS) {
        if (Object.prototype.hasOwnProperty.call(o, key)) {
          delete o[key];
          hit = true;
        }
      }
      return hit;
    };
    // Player responses arrive as JSON (page load, and every in-page navigation), sometimes wrapped.
    const prune = (value) => {
      try {
        if (value && typeof value === 'object') {
          let hit = strip(value) | strip(value.playerResponse);
          if (Array.isArray(value) && value.length < 20) {
            for (const part of value) hit |= strip(part && part.playerResponse);
          }
          if (hit) post({ type: 'adblocked' });
        }
      } catch (_) { /* never break the page */ }
      return value;
    };

    JSON.parse = new Proxy(JSON.parse, {
      apply: (target, self, args) => prune(Reflect.apply(target, self, args)),
    });
    Response.prototype.json = new Proxy(Response.prototype.json, {
      apply: (target, self, args) => Reflect.apply(target, self, args).then(prune),
    });
    let initial;
    try {
      Object.defineProperty(window, 'ytInitialPlayerResponse', {
        configurable: true,
        enumerable: true,
        get: () => initial,
        set: (value) => { initial = prune(value); },
      });
    } catch (_) { /* already defined */ }

    const CSS = [
      '#masthead-ad', '#player-ads',
      'ytd-engagement-panel-section-list-renderer[target-id="engagement-panel-ads"]',
      'ytd-ad-slot-renderer', 'ytd-in-feed-ad-layout-renderer', 'ytd-banner-promo-renderer',
      'ytd-statement-banner-renderer', 'ytd-promoted-sparkles-web-renderer', 'ytd-promoted-video-renderer',
      'ytd-compact-promoted-video-renderer', 'ytd-display-ad-renderer', 'ytd-action-companion-ad-renderer',
      'ytd-player-legacy-desktop-watch-ads-renderer', 'ytd-companion-slot-renderer', 'ytd-primetime-promo-renderer',
      'ytd-search-pyv-renderer', 'ytd-rich-item-renderer:has(ytd-ad-slot-renderer)',
      'ytd-rich-section-renderer:has(ytd-statement-banner-renderer)', 'ytd-reel-video-renderer:has(ytd-ad-slot-renderer)',
      'panel-ad-header-image-lockup-view-model', 'ad-slot-renderer', 'ytm-promoted-sparkles-web-renderer',
      '.ytp-ad-overlay-container', '.ytp-ad-overlay-slot', '.ytp-featured-product', '.ytp-suggested-action',
      'tp-yt-paper-dialog:has(ytd-enforcement-message-view-model)', 'ytd-enforcement-message-view-model',
    ].join(',\n') + ' { display: none !important; }';
    const addStyle = () => {
      if (document.getElementById('snag-adblock') || !document.documentElement) return;
      const style = document.createElement('style');
      style.id = 'snag-adblock';
      style.textContent = CSS;
      (document.head || document.documentElement).appendChild(style);
    };
    addStyle();
    document.addEventListener('readystatechange', addStyle);
    document.addEventListener('DOMContentLoaded', addStyle);

    // Anything that still slips through: muted, fast-forwarded and skipped.
    if (isTop) {
      let inAd = false;
      let savedRate = 1;
      let savedMuted = false;
      setInterval(() => {
        const player = document.querySelector('#movie_player');
        const video = player && player.querySelector('video');
        const adShowing = !!player && (player.classList.contains('ad-showing') || player.classList.contains('ad-interrupting'));
        if (adShowing && video) {
          if (!inAd) {
            inAd = true;
            savedRate = video.playbackRate || 1;
            savedMuted = video.muted;
            post({ type: 'adblocked' });
          }
          video.muted = true;
          if (isFinite(video.duration) && video.duration > 0 && video.currentTime < video.duration - 0.25) {
            video.currentTime = video.duration - 0.1;
          }
          if (video.playbackRate < 16) video.playbackRate = 16;
          for (const button of document.querySelectorAll(
            '.ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-ad-skip-button-container button')) {
            button.click();
          }
        } else if (inAd) {
          inAd = false;
          if (video) {
            video.playbackRate = savedRate;
            video.muted = savedMuted;
          }
        }

        // YouTube's "ad blockers aren't allowed" pop-up: take it away and carry on playing.
        const enforcement = document.querySelector('ytd-enforcement-message-view-model');
        if (enforcement) {
          const dialog = enforcement.closest('tp-yt-paper-dialog');
          if (dialog) dialog.remove();
          enforcement.remove();
          for (const backdrop of document.querySelectorAll('tp-yt-iron-overlay-backdrop')) backdrop.remove();
          if (video && video.paused) video.play().catch(() => {});
        }
      }, 250);
    }
  }

  if (!isTop || !webview) return;

  // ------------------------------------------------------------------ 2. play clicked videos in Snag
  let playInSnag = false;
  let videoInfo = null; // { id, segments: [[start, end], ...], dislikes: '1.2K' or null }, from Snag
  webview.addEventListener('message', (event) => {
    const data = event.data;
    if (!data || typeof data !== 'object') return;
    if (data.type === 'snag-mode') {
      playInSnag = data.playInSnag === true;
    } else if (data.type === 'snag-video' && typeof data.id === 'string' && /^[\w-]{11}$/.test(data.id)) {
      const segments = Array.isArray(data.segments)
        ? data.segments.filter((s) => Array.isArray(s) && s.length === 2 && isFinite(s[0]) && isFinite(s[1]) && s[1] > s[0])
        : [];
      const dislikes = typeof data.dislikes === 'string' && data.dislikes.length <= 12 ? data.dislikes : null;
      videoInfo = { id: data.id, segments, dislikes };
    } else if (data.type === 'snag-pause') {
      for (const video of document.querySelectorAll('video')) {
        try { video.pause(); } catch (_) { /* ignore */ }
      }
    }
  });
  post({ type: 'hello' });

  const CARD = [
    'ytd-rich-item-renderer', 'ytd-video-renderer', 'ytd-compact-video-renderer', 'ytd-grid-video-renderer',
    'ytd-playlist-video-renderer', 'ytd-playlist-panel-video-renderer', 'ytd-reel-item-renderer',
    'yt-lockup-view-model', 'ytm-shorts-lockup-view-model', 'ytm-shorts-lockup-view-model-v2',
  ].join(',');
  const TITLE = '#video-title, #video-title-link, .yt-lockup-metadata-view-model__title, .yt-lockup-metadata-view-model-wiz__title, h3';

  document.addEventListener('click', (event) => {
    if (!playInSnag || event.button !== 0 || event.ctrlKey || event.shiftKey || event.metaKey || event.altKey) return;
    const link = event.target instanceof Element ? event.target.closest('a[href]') : null;
    if (!link || link.closest('#movie_player')) return;
    let url;
    try { url = new URL(link.href, location.href); } catch (_) { return; }
    if (!/(^|\.)youtube\.com$/.test(url.hostname) || !/^\/(watch$|shorts\/)/.test(url.pathname)) return;

    event.preventDefault();
    event.stopImmediatePropagation();
    const card = link.closest(CARD);
    const titleElement = card && card.querySelector(TITLE);
    const title = (titleElement && titleElement.textContent) || link.getAttribute('title') || '';
    post({ type: 'play', url: url.href, title: title.trim().slice(0, 300) });
  }, true);

  // ------------------------------------------------------------------ 4. SponsorBlock and dislike counts
  // Snag looks both up (so this page never talks to those services itself) and sends them with 'snag-video'.
  const currentVideoId = () => {
    if (location.pathname === '/watch') return new URLSearchParams(location.search).get('v');
    const shorts = location.pathname.match(/^\/shorts\/([\w-]{11})/);
    return shorts ? shorts[1] : null;
  };

  const showDislikes = (text) => {
    const button = document.querySelector(
      'ytd-watch-metadata dislike-button-view-model button, ytd-watch-metadata #segmented-dislike-button button');
    if (button) {
      let label = button.querySelector('.snag-dislikes');
      if (text && !label) {
        label = document.createElement('div');
        label.className = 'yt-spec-button-shape-next__button-text-content snag-dislikes';
        button.appendChild(label);
        button.classList.remove('yt-spec-button-shape-next--icon-button');
        button.classList.add('yt-spec-button-shape-next--icon-leading');
      }
      if (label && label.textContent !== (text || '')) label.textContent = text || '';
      if (label && !text) {
        label.remove();
        button.classList.remove('yt-spec-button-shape-next--icon-leading');
        button.classList.add('yt-spec-button-shape-next--icon-button');
      }
    }
    // Shorts show a label under the button instead.
    const shorts = document.querySelector('ytd-reel-video-renderer[is-active] #dislike-button .yt-spec-button-shape-with-label__label span');
    if (shorts && text && shorts.textContent !== text) shorts.textContent = text;
  };

  let lastSkip = 0;
  setInterval(() => {
    const id = currentVideoId();
    const info = videoInfo && videoInfo.id === id ? videoInfo : null;
    showDislikes(info && info.dislikes);
    if (!info || info.segments.length === 0) return;

    const player = document.querySelector(location.pathname === '/watch' ? '#movie_player' : 'ytd-reel-video-renderer[is-active] #shorts-player');
    const video = player && player.querySelector('video');
    if (!video || video.paused || player.classList.contains('ad-showing')) return;
    const t = video.currentTime;
    for (const [start, end] of info.segments) {
      if (t >= start && t < end - 0.5) {
        video.currentTime = end;
        if (Date.now() - lastSkip > 2000) post({ type: 'sponsorskipped' });
        lastSkip = Date.now();
        break;
      }
    }
  }, 250);

  // ------------------------------------------------------------------ 3. one thing playing at a time
  // Only the page's main player with sound (not the muted previews that play when you hover a thumbnail).
  document.addEventListener('play', (event) => {
    const video = event.target;
    if (!(video instanceof HTMLVideoElement) || video.muted) return;
    const player = video.closest('#movie_player, #shorts-player');
    if (player && !player.classList.contains('ad-showing')) post({ type: 'webplay' });
  }, true);
})(__SNAG_ADBLOCK__);
