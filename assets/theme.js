// Applies the saved theme before the page is drawn, so it never flashes the wrong one.
// Choice: "dark" (default) or "light"; kept in this browser only.
(function () {
  var pref = "dark";
  try { pref = localStorage.getItem("gl-theme") || "dark"; } catch (e) { }
  var q = /[?&]theme=(light|dark)/.exec(location.search); if (q) pref = q[1];   // e.g. for screenshots
  if (pref !== "light") pref = "dark";   // anything else (an old "system" choice) means dark
  var dark = pref === "dark";
  document.documentElement.setAttribute("data-theme", dark ? "dark" : "light");
  document.documentElement.setAttribute("data-theme-pref", pref);
  try { if (localStorage.getItem("gl-sb") === "mini") document.documentElement.setAttribute("data-sb", "mini"); } catch (e) { }
})();
