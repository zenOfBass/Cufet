// The .NET runtime's entry point. Boots WebAssembly, exposes the two exported functions, and
// announces that they are callable. It deliberately knows nothing about the page — the editor and
// the buttons live in web/app.js, so this file stays the same whatever the interface becomes.

import { dotnet } from './_framework/dotnet.js';

const { getAssemblyExports, getConfig } = await dotnet.create();
const exports = await getAssemblyExports(getConfig().mainAssemblyName);

globalThis.cufet = {
    run:   source => exports.Cufet.Playground.Runtime.Run(source),
    check: source => exports.Cufet.Playground.Runtime.Check(source),
};

// A flag as well as an event, because the two are loaded as separate modules and this one has a
// top-level await. If the runtime happens to finish booting before app.js has registered its
// listener, the event alone would be missed and the Run button would never enable.
globalThis.cufetReady = true;
document.dispatchEvent(new CustomEvent('cufet-ready'));
