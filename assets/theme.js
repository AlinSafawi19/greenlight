// Applies the saved theme before the page is drawn, so it never flashes the wrong one.
// Choice: "dark" (default), "light" or "system"; kept in this browser only.
(function () {
  var pref = "dark";
  try { pref = localStorage.getItem("gl-theme") || "dark"; } catch (e) { }
  var q = /[?&]theme=(light|dark|system)/.exec(location.search); if (q) pref = q[1];   // e.g. for screenshots
  var dark = pref === "dark" || (pref === "system" && window.matchMedia && matchMedia("(prefers-color-scheme: dark)").matches);
  document.documentElement.setAttribute("data-theme", dark ? "dark" : "light");
  document.documentElement.setAttribute("data-theme-pref", pref);
  try { if (localStorage.getItem("gl-sb") === "mini") document.documentElement.setAttribute("data-sb", "mini"); } catch (e) { }
})();
