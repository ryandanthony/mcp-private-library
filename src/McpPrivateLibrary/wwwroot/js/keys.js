/* ==========================================================================
   MCP Private Library - "API keys" screen module.

   Owns the empty <section id="view-keys"> shell provided by index.html and
   renders the signed-in user's API keys: create, view, revoke.

   API keys are the credential for non-interactive clients (MCP hosts, CLIs)
   that can't run an OAuth code flow. They are presented as
   `Authorization: ApiKey mcpl_<keyId>_<secret>` and are scoped to the user
   who created them.

   Contract with the app shell (js/core.js, loaded first) is the same as the
   other screen modules: App.fetchJson / esc / setAlert / fmtTime / onView.

   Data source: /api/keys (GET list, POST create, DELETE /{id} revoke).
   ========================================================================== */
(function () {
  "use strict";

  var App = window.App;
  if (!App || typeof App.onView !== "function") return; // shell not present

  var KEYS_URL = "/api/keys";

  var currentContainer = null;
  // The plaintext token from the most recent create, held in memory only so it
  // can be re-rendered across list refreshes within this page view. The server
  // stores only a hash and will never return it again; a reload loses it.
  var freshToken = null;

  // ---- Rendering ----------------------------------------------------------

  function statusBadge(key) {
    var state =
      key.status === "Active" ? "completed" : key.status === "Expired" ? "queued" : "failed";
    return '<span class="keys-badge is-' + state + '">' + App.esc(key.status) + "</span>";
  }

  function rowHtml(key) {
    var meta = [];
    meta.push("Created " + (App.fmtTime(key.createdAt) || "unknown"));
    meta.push(key.lastUsedAt ? "Last used " + App.fmtTime(key.lastUsedAt) : "Never used");
    if (key.expiresAt) meta.push("Expires " + App.fmtTime(key.expiresAt));
    if (key.revokedAt) meta.push("Revoked " + App.fmtTime(key.revokedAt));

    return (
      '<li class="keys-row' + (key.active ? "" : " is-inactive") + '">' +
      '  <div class="keys-row-main">' +
      '    <span class="keys-name">' + App.esc(key.name) + "</span>" +
      "    " + statusBadge(key) +
      '    <code class="keys-prefix">' + App.esc(key.prefix) + "</code>" +
      "  </div>" +
      '  <div class="keys-row-meta">' + App.esc(meta.join(" · ")) + "</div>" +
      '  <div class="keys-row-actions">' +
      (key.active
        ? '    <button type="button" class="btn btn-danger keys-revoke" data-id="' +
          App.esc(key.id) +
          '" data-name="' + App.esc(key.name) + '">Revoke</button>'
        : "") +
      "  </div>" +
      "</li>"
    );
  }

  /**
   * Renders the one-time token panel. This is the only moment the plaintext
   * exists client-side, so the copy affordance is deliberately prominent and
   * the warning explicit.
   */
  function tokenPanelHtml() {
    if (!freshToken) return "";
    return (
      '<div class="keys-token" role="status">' +
      '  <div class="keys-token-head">Copy your new key now</div>' +
      '  <p class="keys-token-note">This is the only time it will be shown. The server keeps only a hash of it.</p>' +
      '  <div class="keys-token-value">' +
      '    <code id="keys-token-text">' + App.esc(freshToken) + "</code>" +
      '    <button type="button" class="btn keys-copy" id="keys-copy">Copy</button>' +
      "  </div>" +
      '  <details class="keys-token-usage">' +
      "    <summary>How to use it</summary>" +
      '    <pre class="keys-token-example">' +
      App.esc(
        'curl -H "Authorization: ApiKey ' + freshToken + '" \\\n' +
        "     " + window.location.origin + "/api/repositories\n\n" +
        "MCP client config:\n" +
        JSON.stringify(
          {
            "mcp-private-library": {
              type: "http",
              url: window.location.origin + "/mcp",
              headers: { Authorization: "ApiKey " + freshToken },
            },
          },
          null,
          2
        )
      ) +
      "</pre>" +
      "  </details>" +
      '  <button type="button" class="btn keys-dismiss" id="keys-dismiss">Done</button>' +
      "</div>"
    );
  }

  function render(keys) {
    if (!currentContainer) return;

    var list = keys && keys.length
      ? '<ul class="keys-list">' + keys.map(rowHtml).join("") + "</ul>"
      : '<p class="muted keys-empty">No API keys yet. Create one to let an MCP client or script authenticate as you.</p>';

    currentContainer.innerHTML =
      '<section class="keys-screen" aria-labelledby="keys-heading">' +
      '  <div class="keys-head">' +
      '    <h2 id="keys-heading" class="keys-title">API keys</h2>' +
      "  </div>" +
      '  <p class="muted keys-intro">' +
      "    Keys authenticate non-interactive clients as you, using " +
      '    <code>Authorization: ApiKey &lt;token&gt;</code>. They work anywhere a login does, ' +
      "    and keep working until you revoke them." +
      "  </p>" +
      '  <form id="keys-form" class="keys-form" novalidate>' +
      '    <div class="keys-form-fields">' +
      '      <label class="sr-only" for="keys-name">Key name</label>' +
      '      <input type="text" id="keys-name" placeholder="e.g. jcode on laptop" maxlength="100" autocomplete="off" required />' +
      '      <label class="sr-only" for="keys-expiry">Expiry</label>' +
      '      <select id="keys-expiry">' +
      '        <option value="">No expiry</option>' +
      '        <option value="30">Expires in 30 days</option>' +
      '        <option value="90">Expires in 90 days</option>' +
      '        <option value="365">Expires in 1 year</option>' +
      "      </select>" +
      '      <button type="submit" class="btn btn-primary" id="keys-create">Create key</button>' +
      "    </div>" +
      '    <p id="keys-error" class="error-text" role="alert" hidden></p>' +
      "  </form>" +
      tokenPanelHtml() +
      list +
      "</section>";

    bind();
  }

  // ---- Behaviour ----------------------------------------------------------

  function load() {
    return App.fetchJson(KEYS_URL)
      .then(render)
      .catch(function (err) {
        if (!currentContainer) return;
        currentContainer.innerHTML =
          '<section class="keys-screen"><p class="error-text">' +
          App.esc(err.message) +
          "</p></section>";
      });
  }

  function bind() {
    var form = currentContainer.querySelector("#keys-form");
    var nameInput = currentContainer.querySelector("#keys-name");
    var expirySelect = currentContainer.querySelector("#keys-expiry");
    var createBtn = currentContainer.querySelector("#keys-create");
    var errorEl = currentContainer.querySelector("#keys-error");

    if (form) {
      form.addEventListener("submit", function (e) {
        e.preventDefault();
        App.setAlert(errorEl, "");

        var name = nameInput.value.trim();
        if (!name) {
          App.setAlert(errorEl, "Give the key a name so you can recognise it later.");
          nameInput.focus();
          return;
        }

        var days = expirySelect.value ? Number(expirySelect.value) : null;
        createBtn.disabled = true;
        createBtn.textContent = "Creating…";

        App.fetchJson(KEYS_URL, {
          method: "POST",
          headers: { "Content-Type": "application/json", Accept: "application/json" },
          body: JSON.stringify({ name: name, expiresInDays: days }),
        })
          .then(function (res) {
            freshToken = res && res.token;
            return load();
          })
          .catch(function (err) {
            App.setAlert(errorEl, err.message);
            createBtn.disabled = false;
            createBtn.textContent = "Create key";
          });
      });
    }

    var copyBtn = currentContainer.querySelector("#keys-copy");
    if (copyBtn) {
      copyBtn.addEventListener("click", function () {
        var text = freshToken || "";
        var done = function () {
          copyBtn.textContent = "Copied";
          setTimeout(function () { copyBtn.textContent = "Copy"; }, 1500);
        };
        if (navigator.clipboard && navigator.clipboard.writeText) {
          navigator.clipboard.writeText(text).then(done, selectFallback);
        } else {
          selectFallback();
        }
      });
    }

    // Clipboard API needs a secure context; when unavailable, select the text
    // so the user can copy manually rather than silently doing nothing.
    function selectFallback() {
      var code = currentContainer.querySelector("#keys-token-text");
      if (!code) return;
      var range = document.createRange();
      range.selectNodeContents(code);
      var sel = window.getSelection();
      sel.removeAllRanges();
      sel.addRange(range);
    }

    var dismissBtn = currentContainer.querySelector("#keys-dismiss");
    if (dismissBtn) {
      dismissBtn.addEventListener("click", function () {
        freshToken = null;
        load();
      });
    }

    Array.prototype.forEach.call(
      currentContainer.querySelectorAll(".keys-revoke"),
      function (btn) {
        btn.addEventListener("click", function () {
          var id = btn.getAttribute("data-id");
          var name = btn.getAttribute("data-name");
          // Revocation is immediate and irreversible, and silently breaks any
          // client still using the key, so confirm before firing.
          if (!window.confirm('Revoke "' + name + '"? Any client using it will stop working immediately.')) return;

          btn.disabled = true;
          btn.textContent = "Revoking…";
          App.fetchJson(KEYS_URL + "/" + encodeURIComponent(id), { method: "DELETE" })
            .then(load)
            .catch(function (err) {
              btn.disabled = false;
              btn.textContent = "Revoke";
              window.alert(err.message);
            });
        });
      }
    );
  }

  App.onView("keys", function (container) {
    currentContainer = container;
    load();
  });
})();
