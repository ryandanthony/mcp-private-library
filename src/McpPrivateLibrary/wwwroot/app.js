/* ==========================================================================
   MCP Private Library - front-end logic (vanilla JS, no dependencies)

   Responsibilities:
   - Submit a GitHub URL to POST /api/jobs.
   - Poll GET /api/jobs/{id} every ~1.5s and render live progress.
   - Load "recent jobs" (GET /api/jobs) and "indexed repositories"
     (GET /api/repositories), each refreshable.

   Everything is namespaced inside an IIFE to avoid polluting globals.
   ========================================================================== */
(function () {
  "use strict";

  // ---- Config -------------------------------------------------------------
  var POLL_INTERVAL_MS = 1500;
  // Terminal statuses stop polling.
  var TERMINAL = { Completed: true, Failed: true };

  // ---- Element handles ----------------------------------------------------
  var form = document.getElementById("job-form");
  var urlInput = document.getElementById("repo-url");
  var submitBtn = document.getElementById("submit-btn");
  var formError = document.getElementById("form-error");

  var progressCard = document.getElementById("progress-card");
  var progressJobId = document.getElementById("progress-job-id");
  var progressStatus = document.getElementById("progress-status");
  var progressError = document.getElementById("progress-error");

  var filesCount = document.getElementById("files-count");
  var filesBar = document.getElementById("files-bar");
  var filesFill = document.getElementById("files-fill");

  var chunksCount = document.getElementById("chunks-count");
  var chunksBar = document.getElementById("chunks-bar");
  var chunksFill = document.getElementById("chunks-fill");

  var jobsList = document.getElementById("jobs-list");
  var reposList = document.getElementById("repos-list");
  var refreshJobsBtn = document.getElementById("refresh-jobs");
  var refreshReposBtn = document.getElementById("refresh-repos");

  // Search elements
  var searchForm = document.getElementById("search-form");
  var searchQuery = document.getElementById("search-query");
  var searchRepo = document.getElementById("search-repo");
  var searchTopK = document.getElementById("search-topk");
  var searchBtn = document.getElementById("search-btn");
  var searchError = document.getElementById("search-error");
  var searchResults = document.getElementById("search-results");

  // Repository-search elements
  var repoSearchForm = document.getElementById("repo-search-form");
  var repoSearchQuery = document.getElementById("repo-search-query");
  var repoSearchBtn = document.getElementById("repo-search-btn");
  var repoSearchError = document.getElementById("repo-search-error");
  var repoSearchResults = document.getElementById("repo-search-results");

  // Handle for the active polling timer so we can cancel/replace it.
  var pollTimer = null;

  // ---- Small helpers ------------------------------------------------------

  /**
   * Fetch JSON with basic error normalization.
   * Throws an Error whose message prefers an API-provided {"error": "..."}.
   */
  function fetchJson(url, options) {
    return fetch(url, options).then(function (res) {
      // Attempt to parse a JSON body even on error responses.
      return res
        .json()
        .catch(function () {
          return null;
        })
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

  /** Escape untrusted text before inserting into HTML. */
  function esc(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  /** Map a status string to a coarse badge state for coloring. */
  function badgeState(status) {
    if (status === "Completed") return "completed";
    if (status === "Failed") return "failed";
    if (status === "Queued") return "queued";
    return "active"; // Cloning, Discovering, Chunking, Embedding
  }

  /** Show a message in an alert element (or hide it when message is falsy). */
  function setAlert(el, message) {
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

  // ---- Progress rendering -------------------------------------------------

  /** Update a single progress bar (fill width + ARIA value). */
  function renderBar(fillEl, barEl, countEl, done, total) {
    var p = pct(done, total);
    fillEl.style.width = p + "%";
    barEl.setAttribute("aria-valuenow", String(p));
    countEl.textContent = (Number(done) || 0) + " / " + (Number(total) || 0);
  }

  /** Render the full progress card from a job object. */
  function renderProgress(job) {
    progressCard.hidden = false;
    progressJobId.textContent = job.id != null ? String(job.id) : "—";

    progressStatus.textContent = job.status || "—";
    progressStatus.setAttribute("data-state", badgeState(job.status));

    renderBar(filesFill, filesBar, filesCount, job.filesProcessed, job.filesTotal);
    renderBar(chunksFill, chunksBar, chunksCount, job.chunksEmbedded, job.chunksTotal);

    // Surface job-level error text (present on Failed jobs).
    setAlert(progressError, job.status === "Failed" ? job.error || "Job failed." : "");
  }

  // ---- Polling ------------------------------------------------------------

  function stopPolling() {
    if (pollTimer !== null) {
      clearTimeout(pollTimer);
      pollTimer = null;
    }
  }

  /**
   * Poll a single job until it reaches a terminal status.
   * Refreshes the jobs + repositories lists when the job completes.
   */
  function pollJob(id) {
    stopPolling();

    function tick() {
      fetchJson("/api/jobs/" + encodeURIComponent(id))
        .then(function (job) {
          renderProgress(job);
          if (TERMINAL[job.status]) {
            stopPolling();
            // A finished job may change both lists.
            loadJobs();
            if (job.status === "Completed") loadRepositories();
          } else {
            pollTimer = setTimeout(tick, POLL_INTERVAL_MS);
          }
        })
        .catch(function (err) {
          // Transient errors: show the message but keep trying.
          setAlert(progressError, "Could not fetch job status: " + err.message);
          pollTimer = setTimeout(tick, POLL_INTERVAL_MS);
        });
    }

    tick();
  }

  // ---- Submit handler -----------------------------------------------------

  function onSubmit(event) {
    event.preventDefault();
    setAlert(formError, "");

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
        // Reset progress alert and start tracking the new job.
        setAlert(progressError, "");
        renderProgress({
          id: job.jobId,
          status: job.status || "Queued",
          filesProcessed: 0,
          filesTotal: 0,
          chunksEmbedded: 0,
          chunksTotal: 0,
        });
        progressCard.scrollIntoView({ behavior: "smooth", block: "nearest" });
        pollJob(job.jobId);
        loadJobs(); // Reflect the new job in the recent list right away.
      })
      .catch(function (err) {
        setAlert(formError, err.message);
      })
      .finally(function () {
        submitBtn.disabled = false;
        submitBtn.textContent = "Submit";
      });
  }

  // ---- Recent jobs list ---------------------------------------------------

  function renderJobItem(job) {
    var item = document.createElement("div");
    item.className = "list-item";

    var left = document.createElement("div");
    left.innerHTML =
      '<div class="primary">' +
      esc(job.url || "(unknown url)") +
      "</div>" +
      '<div class="secondary">Job #' +
      esc(job.id) +
      " · updated " +
      esc(fmtTime(job.updatedAt) || "—") +
      "</div>";

    var right = document.createElement("div");
    right.className = "meta";
    var badge =
      '<span class="status-badge" data-state="' +
      esc(badgeState(job.status)) +
      '">' +
      esc(job.status || "—") +
      "</span>";
    var counts =
      '<div class="secondary mono">' +
      (Number(job.filesProcessed) || 0) +
      "/" +
      (Number(job.filesTotal) || 0) +
      " files · " +
      (Number(job.chunksEmbedded) || 0) +
      "/" +
      (Number(job.chunksTotal) || 0) +
      " chunks</div>";
    right.innerHTML = badge + counts;

    item.appendChild(left);
    item.appendChild(right);

    // Clicking a non-terminal job resumes live tracking of it.
    if (!TERMINAL[job.status]) {
      item.style.cursor = "pointer";
      item.title = "Track this job";
      item.addEventListener("click", function () {
        renderProgress(job);
        pollJob(job.id);
        progressCard.scrollIntoView({ behavior: "smooth", block: "nearest" });
      });
    }
    return item;
  }

  function loadJobs() {
    jobsList.setAttribute("aria-busy", "true");
    fetchJson("/api/jobs")
      .then(function (jobs) {
        jobsList.innerHTML = "";
        if (!Array.isArray(jobs) || jobs.length === 0) {
          jobsList.innerHTML = '<p class="muted">No jobs yet.</p>';
          return;
        }
        jobs.forEach(function (job) {
          jobsList.appendChild(renderJobItem(job));
        });
      })
      .catch(function (err) {
        jobsList.innerHTML =
          '<p class="error-text">Could not load jobs: ' + esc(err.message) + "</p>";
      })
      .finally(function () {
        jobsList.removeAttribute("aria-busy");
      });
  }

  // ---- Indexed repositories list ------------------------------------------

  function renderRepoItem(repo) {
    var item = document.createElement("div");
    item.className = "list-item";

    var left = document.createElement("div");
    // Link to the source URL when available; otherwise just show the slug.
    var title = repo.url
      ? '<a href="' +
        esc(repo.url) +
        '" target="_blank" rel="noopener noreferrer">' +
        esc(repo.slug || repo.url) +
        "</a>"
      : esc(repo.slug || "(unknown)");
    left.innerHTML =
      '<div class="primary">' +
      title +
      "</div>" +
      '<div class="secondary mono">#' +
      esc(repo.id || "") +
      "</div>";

    var right = document.createElement("div");
    right.className = "meta";
    right.innerHTML =
      '<div class="secondary mono">' +
      (Number(repo.documents) || 0) +
      " docs · " +
      (Number(repo.chunks) || 0) +
      " chunks</div>";

    item.appendChild(left);
    item.appendChild(right);
    return item;
  }

  function loadRepositories() {
    reposList.setAttribute("aria-busy", "true");
    fetchJson("/api/repositories")
      .then(function (repos) {
        reposList.innerHTML = "";
        if (!Array.isArray(repos) || repos.length === 0) {
          reposList.innerHTML = '<p class="muted">No repositories indexed yet.</p>';
          populateRepoFilter([]);
          return;
        }
        repos.forEach(function (repo) {
          reposList.appendChild(renderRepoItem(repo));
        });
        // Keep the search repository filter in sync with indexed repos.
        populateRepoFilter(repos);
      })
      .catch(function (err) {
        reposList.innerHTML =
          '<p class="error-text">Could not load repositories: ' +
          esc(err.message) +
          "</p>";
      })
      .finally(function () {
        reposList.removeAttribute("aria-busy");
      });
  }

  /** Populate the search repo <select> (value = repo ID), preserving selection when possible. */
  function populateRepoFilter(repos) {
    if (!searchRepo) return;
    var current = searchRepo.value;
    // First option is always "All repositories".
    searchRepo.innerHTML = '<option value="">All repositories</option>';
    (repos || []).forEach(function (repo) {
      var opt = document.createElement("option");
      opt.value = repo.id || "";
      opt.textContent = repo.slug || repo.id || "(unknown)";
      searchRepo.appendChild(opt);
    });
    // Restore prior selection if it still exists.
    if (current) searchRepo.value = current;
  }

  /**
   * Scope the documentation search to a specific repository by ID and focus the query box.
   * Called from repo-search result cards ("Search docs in this repo").
   */
  function scopeDocSearchToRepo(repoId, slug) {
    if (!searchRepo) return;
    // Ensure the option exists even if the repositories list hasn't refreshed yet.
    var found = Array.prototype.some.call(searchRepo.options, function (o) {
      return o.value === repoId;
    });
    if (!found && repoId) {
      var opt = document.createElement("option");
      opt.value = repoId;
      opt.textContent = slug || repoId;
      searchRepo.appendChild(opt);
    }
    searchRepo.value = repoId || "";
    var section = document.getElementById("search-heading");
    if (section) section.scrollIntoView({ behavior: "smooth", block: "start" });
    searchQuery.focus();
  }

  // ---- Semantic search ----------------------------------------------------

  /** Render one search hit as a result card. */
  function renderSearchHit(hit, rank) {
    var item = document.createElement("article");
    item.className = "search-hit";

    var scorePct = Math.round((Number(hit.score) || 0) * 1000) / 10; // one decimal %
    var heading = hit.headingPath ? " &middot; " + esc(hit.headingPath) : "";

    var head = document.createElement("div");
    head.className = "search-hit-head";
    head.innerHTML =
      '<div class="search-hit-loc">' +
      '<span class="rank">' + rank + "</span> " +
      '<span class="mono">' + esc(hit.repositorySlug || "") + "</span> &mdash; " +
      "<span>" + esc(hit.documentPath || "") + "</span>" + heading +
      "</div>" +
      '<span class="score-badge" title="Cosine similarity">' + esc(hit.score != null ? hit.score.toFixed(3) : "—") +
      " (" + scorePct + "%)</span>";

    var body = document.createElement("pre");
    body.className = "search-hit-body";
    body.textContent = hit.content || "";

    item.appendChild(head);
    item.appendChild(body);
    return item;
  }

  function onSearch(event) {
    event.preventDefault();
    setAlert(searchError, "");

    var query = searchQuery.value.trim();
    if (!query) {
      setAlert(searchError, "Please enter a search query.");
      searchQuery.focus();
      return;
    }

    var topK = parseInt(searchTopK.value, 10);
    if (isNaN(topK) || topK < 1) topK = 5;
    if (topK > 50) topK = 50;

    var body = {
      query: query,
      topK: topK,
      repositoryId: searchRepo.value || null,
    };

    searchBtn.disabled = true;
    searchBtn.textContent = "Searching…";
    searchResults.setAttribute("aria-busy", "true");
    searchResults.innerHTML = '<p class="muted">Searching&hellip;</p>';

    fetchJson("/api/search", {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify(body),
    })
      .then(function (data) {
        searchResults.innerHTML = "";
        var hits = (data && data.results) || [];
        if (!hits.length) {
          searchResults.innerHTML =
            '<p class="muted">No matches for &ldquo;' + esc(query) + "&rdquo;.</p>";
          return;
        }
        var summary = document.createElement("p");
        summary.className = "search-summary muted";
        summary.textContent =
          "Found " + hits.length + (hits.length === 1 ? " result" : " results") +
          ' for "' + query + '"';
        searchResults.appendChild(summary);
        hits.forEach(function (hit, i) {
          searchResults.appendChild(renderSearchHit(hit, i + 1));
        });
      })
      .catch(function (err) {
        searchResults.innerHTML = "";
        setAlert(searchError, err.message);
      })
      .finally(function () {
        searchBtn.disabled = false;
        searchBtn.textContent = "Search";
        searchResults.removeAttribute("aria-busy");
      });
  }

  // ---- Repository search --------------------------------------------------

  /** Render one repository-search hit with a button to scope doc search to it. */
  function renderRepoHit(hit, rank) {
    var item = document.createElement("article");
    item.className = "search-hit";

    var head = document.createElement("div");
    head.className = "search-hit-head";
    var titleHtml = hit.url
      ? '<a href="' + esc(hit.url) + '" target="_blank" rel="noopener noreferrer">' + esc(hit.slug || hit.id) + "</a>"
      : esc(hit.slug || hit.id);
    head.innerHTML =
      '<div class="search-hit-loc">' +
      '<span class="rank">' + rank + "</span> " + titleHtml +
      ' <span class="mono muted">#' + esc(hit.id) + "</span>" +
      "</div>" +
      '<span class="score-badge">' + esc(hit.score != null ? hit.score.toFixed(3) : "—") + "</span>";

    var meta = document.createElement("div");
    meta.className = "secondary mono";
    meta.textContent = (Number(hit.documents) || 0) + " docs · " + (Number(hit.chunks) || 0) + " chunks";

    var summary = document.createElement("p");
    summary.className = "repo-hit-summary";
    summary.textContent = hit.summary || "";

    var actions = document.createElement("div");
    actions.className = "form-actions";
    var btn = document.createElement("button");
    btn.type = "button";
    btn.className = "btn btn-ghost";
    btn.textContent = "Search docs in this repo →";
    btn.addEventListener("click", function () {
      scopeDocSearchToRepo(hit.id, hit.slug);
    });
    actions.appendChild(btn);

    item.appendChild(head);
    item.appendChild(meta);
    if (hit.summary) item.appendChild(summary);
    item.appendChild(actions);
    return item;
  }

  function onRepoSearch(event) {
    event.preventDefault();
    setAlert(repoSearchError, "");

    var query = repoSearchQuery.value.trim();
    if (!query) {
      setAlert(repoSearchError, "Please enter a query.");
      repoSearchQuery.focus();
      return;
    }

    repoSearchBtn.disabled = true;
    repoSearchBtn.textContent = "Finding…";
    repoSearchResults.setAttribute("aria-busy", "true");
    repoSearchResults.innerHTML = '<p class="muted">Searching&hellip;</p>';

    fetchJson("/api/repositories/search", {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify({ query: query, topK: 5 }),
    })
      .then(function (data) {
        repoSearchResults.innerHTML = "";
        var hits = (data && data.results) || [];
        if (!hits.length) {
          repoSearchResults.innerHTML =
            '<p class="muted">No repositories matched &ldquo;' + esc(query) + "&rdquo;.</p>";
          return;
        }
        hits.forEach(function (hit, i) {
          repoSearchResults.appendChild(renderRepoHit(hit, i + 1));
        });
      })
      .catch(function (err) {
        repoSearchResults.innerHTML = "";
        setAlert(repoSearchError, err.message);
      })
      .finally(function () {
        repoSearchBtn.disabled = false;
        repoSearchBtn.textContent = "Find";
        repoSearchResults.removeAttribute("aria-busy");
      });
  }

  // ---- Wire up events + initial load --------------------------------------

  form.addEventListener("submit", onSubmit);
  searchForm.addEventListener("submit", onSearch);
  repoSearchForm.addEventListener("submit", onRepoSearch);
  refreshJobsBtn.addEventListener("click", loadJobs);
  refreshReposBtn.addEventListener("click", loadRepositories);

  // Stop polling when the tab is hidden; resume is user-driven (click a job).
  document.addEventListener("visibilitychange", function () {
    if (document.hidden) stopPolling();
  });

  loadJobs();
  loadRepositories();
})();
