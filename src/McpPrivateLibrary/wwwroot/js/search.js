/* ==========================================================================
   Search screen: combines repository-level search and documentation search.
   Owns the #view-search container only. Relies on the shared window.App
   helpers defined by core.js (loaded first).
   ========================================================================== */
(function () {
  "use strict";

  var App = window.App;
  var built = false;

  // Element handles captured at build time so event handlers can reach them.
  var els = {};

  /* ----------------------------------------------------------------------
     Small DOM helpers (local; keep this module self-contained).
     ---------------------------------------------------------------------- */
  function make(tag, attrs, text) {
    var node = document.createElement(tag);
    if (attrs) {
      Object.keys(attrs).forEach(function (k) {
        if (k === "class") node.className = attrs[k];
        else if (k === "text") node.textContent = attrs[k];
        else node.setAttribute(k, attrs[k]);
      });
    }
    if (text != null) node.textContent = text;
    return node;
  }

  function clear(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  function fmtScore(score) {
    var n = typeof score === "number" ? score : Number(score);
    return isFinite(n) ? n.toFixed(3) : "—";
  }

  /* ----------------------------------------------------------------------
     Repository filter dropdown (shared by the doc-search section).
     ---------------------------------------------------------------------- */
  function loadRepoOptions() {
    return App.fetchJson("/api/repositories")
      .then(function (repos) {
        var select = els.docRepo;
        if (!select) return;
        var current = select.value;
        // Rebuild, always keeping the "All repositories" option first.
        clear(select);
        select.appendChild(make("option", { value: "" }, "All repositories"));
        (repos || []).forEach(function (r) {
          select.appendChild(make("option", { value: String(r.id) }, r.slug));
        });
        // Restore previous selection if it still exists.
        if (current) select.value = current;
      })
      .catch(function () {
        /* Non-fatal: the filter simply stays at "All repositories". */
      });
  }

  // Ensure an option exists for a given repo id, then select it.
  function selectRepoOption(id, label) {
    var select = els.docRepo;
    if (!select) return;
    var value = String(id);
    var found = false;
    for (var i = 0; i < select.options.length; i++) {
      if (select.options[i].value === value) { found = true; break; }
    }
    if (!found) {
      select.appendChild(make("option", { value: value }, label || value));
    }
    select.value = value;
  }

  /* ----------------------------------------------------------------------
     Section A: repository-level search.
     ---------------------------------------------------------------------- */
  function renderRepoResults(data) {
    var box = els.repoResults;
    clear(box);

    var results = (data && data.results) || [];
    box.appendChild(
      make(
        "p",
        { class: "search-summary muted" },
        "Query: " + App.esc(data.query || "") + " · " + (data.count || 0) + " result(s)"
      )
    );

    if (!results.length) {
      box.appendChild(make("p", { class: "muted" }, "No repositories matched."));
      return;
    }

    results.forEach(function (r, i) {
      var card = make("div", { class: "search-hit repo-hit" });

      var head = make("div", { class: "search-hit-head" });
      var loc = make("div", { class: "search-hit-loc" });
      loc.appendChild(make("span", { class: "rank" }, String(i + 1)));

      var link = make("a", {
        href: r.url || "#",
        target: "_blank",
        rel: "noopener",
        class: "repo-hit-slug",
      });
      link.textContent = r.slug || r.url || "(unnamed)";
      loc.appendChild(link);
      loc.appendChild(make("code", { class: "repo-hit-id" }, "#" + (r.id != null ? r.id : "")));
      head.appendChild(loc);
      head.appendChild(make("span", { class: "score-badge" }, fmtScore(r.score)));
      card.appendChild(head);

      card.appendChild(
        make(
          "div",
          { class: "repo-hit-meta muted" },
          (r.documents || 0) + " docs · " + (r.chunks || 0) + " chunks"
        )
      );

      if (r.summary) {
        card.appendChild(
          make("p", { class: "repo-hit-summary" }, App.snippet(r.summary, 300))
        );
      }

      var actions = make("div", { class: "repo-hit-actions" });
      var btn = make("button", { type: "button", class: "btn btn-ghost" });
      btn.textContent = "Search docs in this repo →";
      btn.addEventListener("click", function () {
        selectRepoOption(r.id, r.slug);
        if (els.docQuery) {
          els.docQuery.focus();
          els.docQuery.scrollIntoView({ behavior: "smooth", block: "center" });
        }
      });
      actions.appendChild(btn);
      card.appendChild(actions);

      box.appendChild(card);
    });
  }

  function runRepoSearch(ev) {
    if (ev) ev.preventDefault();
    var query = (els.repoQuery.value || "").trim();
    App.setAlert(els.repoAlert, "");
    if (!query) {
      App.setAlert(els.repoAlert, "Please enter a search query.");
      els.repoQuery.focus();
      return;
    }

    els.repoBtn.disabled = true;
    App.fetchJson("/api/repositories/search", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ query: query, topK: 5 }),
    })
      .then(function (data) {
        renderRepoResults(data);
      })
      .catch(function (err) {
        clear(els.repoResults);
        App.setAlert(els.repoAlert, err && err.message ? err.message : "Search failed.");
      })
      .then(function () {
        els.repoBtn.disabled = false;
      });
  }

  /* ----------------------------------------------------------------------
     Section B: documentation (chunk) search.
     ---------------------------------------------------------------------- */
  function renderDocResults(data) {
    var box = els.docResults;
    clear(box);

    var results = (data && data.results) || [];
    box.appendChild(
      make(
        "p",
        { class: "search-summary muted" },
        "Query: " + App.esc(data.query || "") + " · " + (data.count || 0) + " result(s)"
      )
    );

    if (!results.length) {
      box.appendChild(make("p", { class: "muted" }, "No documentation matched."));
      return;
    }

    results.forEach(function (r, i) {
      var card = make("div", { class: "search-hit doc-hit" });

      var head = make("div", { class: "search-hit-head" });
      var loc = make("div", { class: "search-hit-loc" });
      loc.appendChild(make("span", { class: "rank" }, String(i + 1)));
      loc.appendChild(
        make(
          "span",
          { class: "doc-hit-title" },
          (r.repositorySlug || "?") + " — " + (r.documentPath || "")
        )
      );
      head.appendChild(loc);
      head.appendChild(make("span", { class: "score-badge" }, fmtScore(r.score)));
      card.appendChild(head);

      if (r.headingPath) {
        card.appendChild(
          make("div", { class: "doc-hit-heading muted" }, "Heading: " + r.headingPath)
        );
      }

      var repoIdMeta = make("div", { class: "doc-hit-repoid muted" });
      repoIdMeta.appendChild(document.createTextNode("Repo ID: "));
      repoIdMeta.appendChild(
        make("code", null, String(r.repositoryId != null ? r.repositoryId : ""))
      );
      card.appendChild(repoIdMeta);

      // Content shown via textContent on a <pre> so markup/code is never executed.
      var pre = make("pre", { class: "search-hit-body" });
      pre.textContent = r.content != null ? r.content : "";
      card.appendChild(pre);

      box.appendChild(card);
    });
  }

  function runDocSearch(ev) {
    if (ev) ev.preventDefault();
    var query = (els.docQuery.value || "").trim();
    App.setAlert(els.docAlert, "");
    if (!query) {
      App.setAlert(els.docAlert, "Please enter a search query.");
      els.docQuery.focus();
      return;
    }

    var topK = parseInt(els.docTopK.value, 10);
    if (!isFinite(topK) || topK < 1) topK = 5;
    if (topK > 50) topK = 50;

    var repoVal = els.docRepo ? els.docRepo.value : "";
    var repositoryId = repoVal ? repoVal : null;

    els.docBtn.disabled = true;
    App.fetchJson("/api/search", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ query: query, topK: topK, repositoryId: repositoryId }),
    })
      .then(function (data) {
        renderDocResults(data);
      })
      .catch(function (err) {
        clear(els.docResults);
        App.setAlert(els.docAlert, err && err.message ? err.message : "Search failed.");
      })
      .then(function () {
        els.docBtn.disabled = false;
      });
  }

  /* ----------------------------------------------------------------------
     Build the DOM once.
     ---------------------------------------------------------------------- */
  function build(container) {
    clear(container);

    /* --- Section A: Find a repository --- */
    var secA = make("section", { class: "search-section", "aria-labelledby": "search-repo-heading" });
    secA.appendChild(make("h2", { id: "search-repo-heading" }, "Find a repository"));
    secA.appendChild(
      make(
        "p",
        { class: "muted" },
        "Search across indexed repositories by what they do, then jump into their docs."
      )
    );

    var repoForm = make("form", { id: "search-repo-form", novalidate: "" });
    var repoRow = make("div", { class: "search-controls" });

    var repoField = make("div", { class: "field" });
    repoField.appendChild(make("label", { for: "search-repo-query" }, "Query"));
    els.repoQuery = make("input", {
      type: "search",
      id: "search-repo-query",
      name: "query",
      placeholder: "e.g. message bus for .NET, or MCP server SDK",
      autocomplete: "off",
    });
    repoField.appendChild(els.repoQuery);
    repoRow.appendChild(repoField);

    var repoActions = make("div", { class: "form-actions" });
    els.repoBtn = make("button", { type: "submit", id: "search-repo-btn", class: "btn btn-primary" }, "Find");
    repoActions.appendChild(els.repoBtn);
    repoRow.appendChild(repoActions);
    repoForm.appendChild(repoRow);

    els.repoAlert = make("p", { id: "search-repo-alert", class: "error-text", role: "alert", hidden: "" });
    repoForm.appendChild(els.repoAlert);
    secA.appendChild(repoForm);

    els.repoResults = make("div", { id: "search-repo-results", class: "search-results", "aria-live": "polite" });
    secA.appendChild(els.repoResults);
    container.appendChild(secA);

    /* --- Section B: Search documentation --- */
    var secB = make("section", { class: "search-section", "aria-labelledby": "search-doc-heading" });
    secB.appendChild(make("h2", { id: "search-doc-heading" }, "Search documentation"));

    var docForm = make("form", { id: "search-doc-form", novalidate: "" });

    var docQueryField = make("div", { class: "field" });
    docQueryField.appendChild(make("label", { for: "search-doc-query" }, "Query"));
    els.docQuery = make("input", {
      type: "search",
      id: "search-doc-query",
      name: "query",
      placeholder: "e.g. how do I create an MCP server with stdio transport?",
      autocomplete: "off",
    });
    docQueryField.appendChild(els.docQuery);
    docForm.appendChild(docQueryField);

    var docRow = make("div", { class: "search-controls" });

    var repoSelField = make("div", { class: "field" });
    repoSelField.appendChild(make("label", { for: "search-doc-repo" }, "Repository"));
    els.docRepo = make("select", { id: "search-doc-repo", name: "repositoryId" });
    els.docRepo.appendChild(make("option", { value: "" }, "All repositories"));
    repoSelField.appendChild(els.docRepo);
    docRow.appendChild(repoSelField);

    var topkField = make("div", { class: "field field-narrow" });
    topkField.appendChild(make("label", { for: "search-doc-topk" }, "Results"));
    els.docTopK = make("input", {
      type: "number",
      id: "search-doc-topk",
      name: "topK",
      value: "5",
      min: "1",
      max: "50",
    });
    topkField.appendChild(els.docTopK);
    docRow.appendChild(topkField);

    var docActions = make("div", { class: "form-actions" });
    els.docBtn = make("button", { type: "submit", id: "search-doc-btn", class: "btn btn-primary" }, "Search");
    docActions.appendChild(els.docBtn);
    docRow.appendChild(docActions);
    docForm.appendChild(docRow);

    els.docAlert = make("p", { id: "search-doc-alert", class: "error-text", role: "alert", hidden: "" });
    docForm.appendChild(els.docAlert);
    secB.appendChild(docForm);

    els.docResults = make("div", { id: "search-doc-results", class: "search-results", "aria-live": "polite" });
    secB.appendChild(els.docResults);
    container.appendChild(secB);

    /* --- Wire events --- */
    repoForm.addEventListener("submit", runRepoSearch);
    docForm.addEventListener("submit", runDocSearch);

    loadRepoOptions();
  }

  /* ----------------------------------------------------------------------
     View registration.
     ---------------------------------------------------------------------- */
  App.onView("search", function (container) {
    if (!built) {
      build(container);
      built = true;
    } else {
      // Refresh the repo filter each time the view becomes active.
      loadRepoOptions();
    }
    if (els.repoQuery) els.repoQuery.focus();
  });
})();
