/* ==========================================================================
   MCP Private Library - app shell / core
   Provides the global `window.App` contract used by the per-screen modules
   (js/repos.js, js/search.js), a tiny hash-based router with a left nav, and
   the landing screen (submit a repo URL).

   Loaded BEFORE the screen modules. Screen modules register themselves with
   App.onView(name, fn) and build their DOM inside their view container.
   ========================================================================== */
(function () {
  "use strict";

  // ---- Shared helpers -----------------------------------------------------

  /**
   * Fetch JSON with error normalization. Rejects with an Error whose message
   * prefers an API-provided {"error":"..."} body.
   */
  function fetchJson(url, options) {
    return fetch(url, options).then(function (res) {
      if (res.status === 401) {
        // Session expired/missing: send the user through the OIDC login flow and
        // bring them back to where they were.
        window.location.href = "/auth/login?returnUrl=" + encodeURIComponent(window.location.href);
        // Never resolves; navigation is already underway.
        return new Promise(function () {});
      }
      return res
        .json()
        .catch(function () { return null; })
        .then(function (body) {
          if (!res.ok) {
            var msg =
              (body && body.error) ||
              "Request failed (" + res.status + " " + res.statusText + ")";
            var err = new Error(msg);
            err.status = res.status;
            throw err;
          }
          return body;
        });
    });
  }

  /** Escape untrusted text before inserting into HTML. */
  function esc(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  /** Clamp a ratio into an integer percentage 0..100. */
  function pct(done, total) {
    var d = Number(done) || 0;
    var t = Number(total) || 0;
    if (t <= 0) return 0;
    var p = Math.round((d / t) * 100);
    if (p < 0) return 0;
    if (p > 100) return 100;
    return p;
  }

  /** Show a message in an alert element (or hide it when message is falsy). */
  function setAlert(el, message) {
    if (!el) return;
    if (message) {
      el.textContent = message;
      el.hidden = false;
    } else {
      el.textContent = "";
      el.hidden = true;
    }
  }

  /** Format a timestamp; returns "" if unparseable. */
  function fmtTime(iso) {
    if (!iso) return "";
    var d = new Date(iso);
    if (isNaN(d.getTime())) return "";
    return d.toLocaleString();
  }

  /** Map a status string to a coarse badge state for coloring. */
  function badgeState(status) {
    if (status === "Completed") return "completed";
    if (status === "Failed") return "failed";
    if (status === "Queued") return "queued";
    if (status === "None") return "queued";
    return "active"; // Cloning, Discovering, Chunking, Embedding
  }

  /** Collapse whitespace and trim to `max` chars with an ellipsis. */
  function snippet(text, max) {
    if (text == null || text === "") return "";
    var collapsed = String(text)
      .split(/\s+/)
      .filter(Boolean)
      .join(" ");
    max = max || 300;
    return collapsed.length <= max ? collapsed : collapsed.slice(0, max).replace(/\s+\S*$/, "") + "…";
  }

  // ---- View registry + hash router ---------------------------------------

  // name -> { container, handlers: [fn], built: bool }
  var VIEW_NAMES = ["home", "repos", "search"];
  var views = {};
  var currentView = null;

  function ensureView(name) {
    if (!views[name]) views[name] = { container: null, handlers: [], built: false };
    return views[name];
  }

  /** Register a handler run every time `name` becomes the active view. */
  function onView(name, fn) {
    ensureView(name).handlers.push(fn);
    // If this view is already the active one at registration time, run now.
    if (currentView === name) {
      var v = views[name];
      if (v.container) fn(v.container);
    }
  }

  function normalize(hash) {
    var name = (hash || "").replace(/^#\/?/, "").trim();
    if (VIEW_NAMES.indexOf(name) === -1) return "home";
    return name;
  }

  function navigate(name) {
    if (VIEW_NAMES.indexOf(name) === -1) name = "home";
    if (("#/" + name) === window.location.hash) {
      // Hash unchanged: activate directly (hashchange won't fire).
      activate(name);
    } else {
      window.location.hash = "#/" + name;
    }
  }

  function activate(name) {
    currentView = name;

    // Toggle view sections.
    VIEW_NAMES.forEach(function (n) {
      var v = views[n];
      if (v && v.container) v.container.hidden = n !== name;
    });

    // Toggle nav active state.
    var links = document.querySelectorAll("[data-nav]");
    Array.prototype.forEach.call(links, function (a) {
      var active = a.getAttribute("data-nav") === name;
      a.classList.toggle("active", active);
      if (active) a.setAttribute("aria-current", "page");
      else a.removeAttribute("aria-current");
    });

    // Run the view's handlers.
    var view = views[name];
    if (view && view.container) {
      view.handlers.forEach(function (fn) {
        try { fn(view.container); }
        catch (e) { /* keep other views working */ console && console.error && console.error(e); }
      });
    }
  }

  function onHashChange() {
    activate(normalize(window.location.hash));
  }

  // ---- Landing screen (submit a repo URL) --------------------------------

  function initHome(container) {
    // Build once.
    if (views.home.built) {
      var qi = container.querySelector("#repo-url");
      if (qi) qi.focus();
      return;
    }
    views.home.built = true;

    container.innerHTML =
      '<section class="card home-card" aria-labelledby="home-heading">' +
      '  <h2 id="home-heading">Index a repository</h2>' +
      '  <p class="muted">Paste a GitHub repo URL (HTTPS or SSH) to clone it and index its Markdown for semantic search.</p>' +
      '  <form id="job-form" novalidate>' +
      '    <div class="field">' +
      '      <label for="repo-url" class="sr-only">GitHub URL</label>' +
      '      <input type="url" id="repo-url" name="url" placeholder="https://github.com/org/repo" autocomplete="off" spellcheck="false" required />' +
      '    </div>' +
      '    <div class="form-actions">' +
      '      <button type="submit" id="submit-btn" class="btn btn-primary">Submit</button>' +
      '    </div>' +
      '    <p id="form-error" class="error-text" role="alert" hidden></p>' +
      '    <p id="form-ok" class="ok-text" role="status" hidden></p>' +
      '  </form>' +
      "</section>";

    var form = container.querySelector("#job-form");
    var urlInput = container.querySelector("#repo-url");
    var submitBtn = container.querySelector("#submit-btn");
    var formError = container.querySelector("#form-error");
    var formOk = container.querySelector("#form-ok");

    form.addEventListener("submit", function (event) {
      event.preventDefault();
      setAlert(formError, "");
      setAlert(formOk, "");

      var url = urlInput.value.trim();
      if (!url) {
        setAlert(formError, "Please enter a GitHub URL.");
        urlInput.focus();
        return;
      }

      submitBtn.disabled = true;
      submitBtn.textContent = "Submitting…";

      fetchJson("/api/jobs", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        body: JSON.stringify({ url: url }),
      })
        .then(function (job) {
          urlInput.value = "";
          setAlert(formOk, "Queued job #" + job.jobId + ". Track progress on the Repositories screen.");
          // Take the user to the repos screen to watch progress.
          navigate("repos");
        })
        .catch(function (err) {
          setAlert(formError, err.message);
        })
        .finally(function () {
          submitBtn.disabled = false;
          submitBtn.textContent = "Submit";
        });
    });

    urlInput.focus();
  }

  // ---- Auth bar (login/logout state in the sidebar) -----------------------

  /**
   * Renders the sidebar sign-in/out control from an already-fetched /auth/me
   * payload (or null when auth is disabled/unreachable, which leaves it empty).
   */
  function renderAuthBar(info) {
    var el = document.getElementById("auth-bar");
    if (!el) return;

    if (info && info.authenticated) {
      el.innerHTML =
        '<span class="auth-user" title="' + esc(info.email || "") + '">' +
        esc(info.name || info.email || "Signed in") +
        "</span>" +
        '<form method="post" action="/auth/logout"><button type="submit" class="auth-link">Sign out</button></form>';
    } else if (info) {
      el.innerHTML =
        '<a class="auth-link" href="/auth/login?returnUrl=' +
        encodeURIComponent(window.location.href) +
        '">Sign in</a>';
    }
  }

  /**
   * Checks auth state once at boot, before anything else renders. Landing on
   * "Index a repo" makes no API calls of its own, so without this eager check
   * an unauthenticated visitor would see a fully-usable-looking page and only
   * discover they're logged out once some other action happens to hit a
   * protected endpoint (e.g. switching tabs and back triggering a poll) —
   * confusing and easy to miss. Redirects to Keycloak immediately instead.
   *
   * Resolves to the /auth/me payload (or null if auth is disabled/unreachable,
   * e.g. local dev) so callers that also want to render the auth bar don't
   * need a second fetch.
   */
  function requireAuthOrRedirect() {
    return fetch("/auth/me", { headers: { Accept: "application/json" } })
      .then(function (res) { return res.ok ? res.json() : null; })
      .then(function (info) {
        if (info && !info.authenticated) {
          window.location.href = "/auth/login?returnUrl=" + encodeURIComponent(window.location.href);
          return new Promise(function () {}); // navigation in progress; never resolve
        }
        return info;
      })
      .catch(function () { return null; }); // auth disabled or /auth/me unreachable: proceed unauthenticated
  }

  // ---- Boot ---------------------------------------------------------------

  function boot() {
    // Bind view containers.
    ensureView("home").container = document.getElementById("view-home");
    ensureView("repos").container = document.getElementById("view-repos");
    ensureView("search").container = document.getElementById("view-search");

    requireAuthOrRedirect().then(renderAuthBar);

    // Register the landing screen.
    onView("home", initHome);

    window.addEventListener("hashchange", onHashChange);

    // Nav clicks that use hrefs like #/repos are handled by hashchange, but we
    // also intercept for the no-op case (same hash re-click) to re-activate.
    document.addEventListener("click", function (e) {
      var a = e.target.closest && e.target.closest("[data-nav]");
      if (!a) return;
      var name = a.getAttribute("data-nav");
      if (("#/" + name) === window.location.hash) {
        e.preventDefault();
        activate(name);
      }
    });

    // Initial activation from the current hash (defaults to home).
    activate(normalize(window.location.hash));
  }

  // Public API for screen modules.
  window.App = {
    fetchJson: fetchJson,
    esc: esc,
    pct: pct,
    setAlert: setAlert,
    fmtTime: fmtTime,
    badgeState: badgeState,
    snippet: snippet,
    onView: onView,
    navigate: navigate,
    views: views,
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
