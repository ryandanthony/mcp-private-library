/* ==========================================================================
   MCP Private Library - "Indexed repositories" screen module.

   Owns the empty <section id="view-repos"> shell provided by index.html and
   populates it with ONE compact line per submitted repository, merging the
   old "indexed repositories" + "job progress" + "recent jobs" views.

   Contract with the app shell (js/core.js, loaded first):
     window.App.fetchJson(url, opts?) -> Promise<any>   (rejects w/ Error.message)
     window.App.esc(value) -> string                    (HTML-escape)
     window.App.setAlert(el, message)                   (show/hide alert text)
     window.App.fmtTime(iso) -> string                  ("" when unparseable)
     window.App.badgeState(status) -> queued|active|completed|failed
     window.App.pct(done, total) -> int 0..100
     window.App.onView(name, fn)                         (fn(container) on activate)
     window.App.navigate(name)

   Data source: GET /api/repos/overview -> array of repo overview objects.

   Everything lives inside an IIFE; no globals leak. In this shell App.views
   is the router's own registry, so this module deliberately never writes it.
   ========================================================================== */
(function () {
  "use strict";

  var App = window.App;
  if (!App || typeof App.onView !== "function") return; // shell not present

  // ---- Config -------------------------------------------------------------
  var OVERVIEW_URL = "/api/repos/overview";
  var POLL_INTERVAL_MS = 1500;
  var ERROR_MAX = 200; // truncate Failed error text to keep rows one line-ish

  // Statuses that never change on their own; when ALL repos are terminal we
  // stop polling. "None" = repo has no job record yet.
  var TERMINAL = { Completed: true, Failed: true, Cancelled: true, None: true };

  // ---- Module-level state -------------------------------------------------
  var currentContainer = null; // the #view-repos element while this view is active
  var pollTimer = null;        // active setTimeout handle (null when idle)
  var generation = 0;          // bumped on every (re)start to cancel stale loops

  // ---- Small helpers ------------------------------------------------------

  /** Coerce to a finite number, defaulting to 0. */
  function num(v) {
    var n = Number(v);
    return isFinite(n) ? n : 0;
  }

  /** Truncate long strings on a word boundary with an ellipsis. */
  function truncate(value, max) {
    var s = String(value == null ? "" : value);
    if (s.length <= max) return s;
    return s.slice(0, max - 1).replace(/\s+\S*$/, "") + "\u2026";
  }

  function stopPolling() {
    if (pollTimer !== null) {
      clearTimeout(pollTimer);
      pollTimer = null;
    }
  }

  /** True if at least one repo is still working (non-terminal status). */
  function anyNonTerminal(list) {
    if (!Array.isArray(list)) return false;
    for (var i = 0; i < list.length; i++) {
      var r = list[i];
      if (!TERMINAL[r && r.status]) return true;
    }
    return false;
  }

  /**
   * True only while our view is the active, visible one. Used to gate polling
   * so we never keep timers running for a hidden tab or a navigated-away view.
   */
  function isViewActive(container) {
    if (!container || container !== currentContainer) return false;
    if (document.hidden) return false;
    if (!container.isConnected) return false;
    // The shell hides inactive views; a hidden view computes display:none
    // (whether via the `hidden` attribute, display:none, or a class).
    try {
      if (window.getComputedStyle(container).display === "none") return false;
    } catch (e) {
      /* getComputedStyle can throw in exotic environments; ignore. */
    }
    return true;
  }

  // ---- Scaffold -----------------------------------------------------------

  /**
   * Ensure the static screen chrome exists inside the container. Idempotent:
   * safe to call on every App.onView("repos") activation. Wires the Refresh
   * button exactly once.
   */
  function buildScaffold(container) {
    if (container.querySelector(".repos-screen")) return; // already built

    container.innerHTML =
      '<div class="repos-screen">' +
      '  <div class="repos-head">' +
      '    <h2 class="repos-title">Indexed repositories</h2>' +
      '    <button type="button" class="repos-refresh">Refresh</button>' +
      "  </div>" +
      '  <p class="repos-error" role="alert" hidden></p>' +
      '  <div class="repos-list" aria-live="polite" aria-busy="false">' +
      '    <p class="repos-empty">Loading\u2026</p>' +
      "  </div>" +
      "</div>";

    var refreshBtn = container.querySelector(".repos-refresh");
    if (refreshBtn) {
      refreshBtn.addEventListener("click", function () {
        // Manual refresh restarts the load/poll cycle from scratch.
        start(container);
      });
    }

    // Event delegation on the list: rows (and their Reindex/Cancel buttons) are
    // replaced wholesale on every render, so bind once on the stable parent.
    var listEl = container.querySelector(".repos-list");
    if (listEl) {
      listEl.addEventListener("click", function (e) {
        var reindexBtn = e.target.closest && e.target.closest(".repo-reindex");
        if (reindexBtn && !reindexBtn.disabled) {
          onReindexClick(container, reindexBtn);
          return;
        }
        var cancelBtn = e.target.closest && e.target.closest(".repo-cancel");
        if (cancelBtn && !cancelBtn.disabled) {
          onCancelClick(container, cancelBtn);
        }
      });
    }
  }

  /**
   * Handles a Reindex button click: confirms, submits the reindex job, then
   * restarts the load/poll cycle so progress shows immediately.
   */
  function onReindexClick(container, btn) {
    var repoId = btn.getAttribute("data-repo-id");
    var slug = btn.getAttribute("data-repo-slug") || repoId;
    if (!repoId) return;

    var ok = window.confirm(
      "Reindex " + slug + "?\n\n" +
      "This re-clones and re-embeds the repository's docs. The current index " +
      "stays fully searchable until the new one is ready, then swaps in atomically " +
      "\u2014 nothing is ever left empty or half-built."
    );
    if (!ok) return;

    var errEl = container.querySelector(".repos-error");
    btn.disabled = true;
    btn.textContent = "Queuing\u2026";

    App.fetchJson("/api/repositories/" + encodeURIComponent(repoId) + "/reindex", {
      method: "POST",
      headers: { Accept: "application/json" },
    })
      .then(function () {
        if (errEl) App.setAlert(errEl, "");
        // Restart the cycle so the new job's progress starts showing right away.
        start(container);
      })
      .catch(function (err) {
        if (errEl) App.setAlert(errEl, "Could not start reindex: " + err.message);
        btn.disabled = false;
        btn.textContent = "Reindex";
      });
  }

  /**
   * Handles a Cancel button click: confirms, then stops the in-flight (or
   * still-queued) job via POST /api/jobs/{id}/cancel. Restarts the load/poll
   * cycle so the row immediately reflects the Cancelled status.
   */
  function onCancelClick(container, btn) {
    var jobId = btn.getAttribute("data-job-id");
    var slug = btn.getAttribute("data-repo-slug") || jobId;
    if (!jobId) return;

    var ok = window.confirm(
      "Cancel indexing for " + slug + "?\n\n" +
      "This stops the crawl/embedding job in place. Any docs already indexed " +
      "in the previous generation are unaffected."
    );
    if (!ok) return;

    var errEl = container.querySelector(".repos-error");
    btn.disabled = true;
    btn.textContent = "Cancelling\u2026";

    App.fetchJson("/api/jobs/" + encodeURIComponent(jobId) + "/cancel", {
      method: "POST",
      headers: { Accept: "application/json" },
    })
      .then(function () {
        if (errEl) App.setAlert(errEl, "");
        start(container);
      })
      .catch(function (err) {
        if (errEl) App.setAlert(errEl, "Could not cancel job: " + err.message);
        btn.disabled = false;
        btn.textContent = "Cancel";
      });
  }

  // ---- Rendering ----------------------------------------------------------

  /** Build the identity cell: slug (linked when a URL exists) + short id. */
  function identHtml(repo) {
    var slug = repo.slug || repo.url || "(unknown)";
    var slugHtml = repo.url
      ? '<a class="repo-slug" href="' +
        App.esc(repo.url) +
        '" target="_blank" rel="noopener noreferrer">' +
        App.esc(slug) +
        "</a>"
      : '<span class="repo-slug">' + App.esc(slug) + "</span>";

    var idHtml = repo.id
      ? '<span class="repo-id mono">#' + App.esc(repo.id) + "</span>"
      : "";

    return '<div class="repo-ident">' + slugHtml + idHtml + "</div>";
  }

  /** Status badge; data-state comes from App.badgeState per the shell contract. */
  function badgeHtml(repo) {
    var status = repo.status || "None";
    var state = App.badgeState(status);
    return (
      '<span class="repo-badge" data-state="' +
      App.esc(state) +
      '" data-status="' +
      App.esc(status) +
      '">' +
      App.esc(status) +
      "</span>"
    );
  }

  /** A slim, ARIA-described progress bar driven by the given percentage. */
  function barHtml(pctVal, label) {
    return (
      '<div class="repo-bar" role="progressbar" aria-label="' +
      App.esc(label) +
      '" aria-valuemin="0" aria-valuemax="100" aria-valuenow="' +
      pctVal +
      '">' +
      '<span class="repo-bar-fill" style="width:' +
      pctVal +
      '%"></span>' +
      "</div>"
    );
  }

  /** The progress cell varies by status (active vs completed vs failed vs none). */
  function progressHtml(repo) {
    var status = repo.status || "None";
    var docs = num(repo.documents);
    var chunks = num(repo.chunks);

    if (status === "Failed") {
      var errText = repo.error ? String(repo.error) : "Indexing failed.";
      return (
        '<div class="repo-progress">' +
        '<span class="repo-error-text" title="' +
        App.esc(errText) +
        '">' +
        App.esc(truncate(errText, ERROR_MAX)) +
        "</span>" +
        "</div>"
      );
    }

    if (status === "Cancelled") {
      return (
        '<div class="repo-progress">' +
        '<span class="repo-counts muted">Cancelled \u00b7 ' +
        docs +
        " docs \u00b7 " +
        chunks +
        " chunks</span>" +
        "</div>"
      );
    }

    if (status === "Completed") {
      return (
        '<div class="repo-progress">' +
        '<span class="repo-counts mono">' +
        docs +
        " docs \u00b7 " +
        chunks +
        " chunks</span>" +
        "</div>"
      );
    }

    if (status === "None") {
      // Terminal but never had a job: show counts if we have any, else a hint.
      var body =
        docs > 0 || chunks > 0
          ? '<span class="repo-counts mono">' +
            docs +
            " docs \u00b7 " +
            chunks +
            " chunks</span>"
          : '<span class="repo-counts muted">No indexing job yet</span>';
      return '<div class="repo-progress">' + body + "</div>";
    }

    // Non-terminal: Queued / Cloning / Discovering / Chunking / Embedding.
    // Drive the bar by embedding progress once chunks are known, else by files.
    var filesTotal = num(repo.filesTotal);
    var chunksTotal = num(repo.chunksTotal);
    var p =
      chunksTotal > 0
        ? App.pct(repo.chunksEmbedded, repo.chunksTotal)
        : App.pct(repo.filesProcessed, filesTotal);

    return (
      '<div class="repo-progress">' +
      barHtml(p, status + " progress") +
      '<span class="repo-counts mono">files ' +
      num(repo.filesProcessed) +
      "/" +
      filesTotal +
      " \u00b7 chunks " +
      num(repo.chunksEmbedded) +
      "/" +
      chunksTotal +
      "</span>" +
      "</div>"
    );
  }

  /** The "updated" timestamp cell. */
  function updatedHtml(repo) {
    var t = App.fmtTime(repo.updatedAt);
    return (
      '<time class="repo-updated" datetime="' +
      App.esc(repo.updatedAt || "") +
      '">' +
      App.esc(t || "\u2014") +
      "</time>"
    );
  }

  /** The "Last indexed" cell: when the currently-live index generation last completed. */
  function lastIndexedHtml(repo) {
    var t = App.fmtTime(repo.lastIndexedAt);
    return (
      '<div class="repo-last-indexed">' +
      '<span class="repo-last-indexed-label">Last indexed</span>' +
      '<time datetime="' +
      App.esc(repo.lastIndexedAt || "") +
      '">' +
      App.esc(t || "Never") +
      "</time>" +
      "</div>"
    );
  }

  /**
   * Reindex button. Disabled while a job for this repo is already in flight (non-terminal
   * status) to avoid queuing overlapping ingestion runs against the same repo.
   */
  function reindexButtonHtml(repo) {
    var state = App.badgeState(repo.status || "None");
    var busy = state === "queued" || state === "active";
    return (
      '<button type="button" class="repo-reindex" data-repo-id="' +
      App.esc(repo.id || "") +
      '" data-repo-slug="' +
      App.esc(repo.slug || repo.url || "") +
      '"' +
      (busy ? " disabled" : "") +
      ">" +
      (busy ? "Indexing\u2026" : "Reindex") +
      "</button>"
    );
  }

  /**
   * Cancel button: only rendered while a job for this repo is actually in flight
   * (queued or actively processing) and we have a jobId to target. Stops the job
   * in place via POST /api/jobs/{id}/cancel rather than killing the process.
   */
  function cancelButtonHtml(repo) {
    var state = App.badgeState(repo.status || "None");
    var busy = state === "queued" || state === "active";
    if (!busy || !repo.jobId) return "";
    return (
      '<button type="button" class="repo-cancel" data-job-id="' +
      App.esc(repo.jobId) +
      '" data-repo-slug="' +
      App.esc(repo.slug || repo.url || "") +
      '">Cancel</button>'
    );
  }

  /** One compact single-line row for a repo. */
  function rowHtml(repo) {
    var state = App.badgeState(repo.status || "None");
    return (
      '<div class="repo-row" data-state="' +
      App.esc(state) +
      '">' +
      identHtml(repo) +
      badgeHtml(repo) +
      progressHtml(repo) +
      lastIndexedHtml(repo) +
      updatedHtml(repo) +
      reindexButtonHtml(repo) +
      cancelButtonHtml(repo) +
      "</div>"
    );
  }

  /** Render the full list (or the empty state) into the container. */
  function renderList(container, list) {
    var listEl = container.querySelector(".repos-list");
    if (!listEl) return;

    if (!Array.isArray(list) || list.length === 0) {
      listEl.innerHTML =
        '<p class="repos-empty muted">No repositories indexed yet.</p>';
      return;
    }

    var html = "";
    for (var i = 0; i < list.length; i++) {
      html += rowHtml(list[i]);
    }
    listEl.innerHTML = html;
  }

  // ---- Fetch + poll cycle -------------------------------------------------

  /**
   * Fetch the overview once and render it. Resolves with the (array) data so
   * the caller can decide whether to keep polling. Rejects on fetch error
   * (after showing an inline error line).
   */
  function fetchAndRender(container) {
    var listEl = container.querySelector(".repos-list");
    var errEl = container.querySelector(".repos-error");
    if (listEl) listEl.setAttribute("aria-busy", "true");

    return App.fetchJson(OVERVIEW_URL)
      .then(function (data) {
        if (errEl) App.setAlert(errEl, "");
        var list = Array.isArray(data) ? data : [];
        renderList(container, list);
        return list;
      })
      .catch(function (err) {
        // Keep any previously rendered rows; surface the error inline.
        if (errEl) {
          App.setAlert(errEl, "Could not load repositories: " + err.message);
        }
        throw err;
      })
      .finally(function () {
        if (listEl) listEl.setAttribute("aria-busy", "false");
      });
  }

  /**
   * A single poll step: fetch, render, then reschedule iff the view is still
   * active AND something is still in progress. `gen` guards against stale
   * loops left over from a previous start()/resume().
   */
  function poll(container, gen) {
    if (gen !== generation) return;

    fetchAndRender(container)
      .then(function (list) {
        if (gen !== generation) return;
        if (isViewActive(container) && anyNonTerminal(list)) {
          pollTimer = setTimeout(function () {
            poll(container, gen);
          }, POLL_INTERVAL_MS);
        } else {
          stopPolling();
        }
      })
      .catch(function () {
        if (gen !== generation) return;
        // Transient error: keep retrying while the view is visible.
        if (isViewActive(container)) {
          pollTimer = setTimeout(function () {
            poll(container, gen);
          }, POLL_INTERVAL_MS);
        } else {
          stopPolling();
        }
      });
  }

  /** (Re)start the load + poll cycle for a container. Cancels any prior loop. */
  function start(container) {
    stopPolling();
    generation += 1; // invalidate any in-flight loop from before
    poll(container, generation);
  }

  // ---- Visibility handling (single, module-wide listener) -----------------
  document.addEventListener("visibilitychange", function () {
    if (document.hidden) {
      // Pause work while the tab is backgrounded.
      stopPolling();
    } else if (currentContainer && isViewActive(currentContainer)) {
      // Resume: refresh immediately and continue polling if needed.
      start(currentContainer);
    }
  });

  // ---- View activation ----------------------------------------------------
  // NOTE: We intentionally do NOT touch App.views here. In this shell,
  // App.views is the router's internal registry (name -> {container, handlers,
  // built}); writing App.views.repos would clobber the repos entry and break
  // onView/activate. This module keeps its entire surface inside the IIFE.
  App.onView("repos", function (container) {
    currentContainer = container;
    buildScaffold(container);
    start(container); // fresh load; polling continues only while non-terminal
  });
})();
