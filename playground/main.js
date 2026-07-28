// Boots the .NET runtime and hands the two exported functions to whatever is on the page.
// There is no UI here yet — this cut of the project exists to answer one question: how many
// megabytes is a Cufet interpreter in a browser?

import { dotnet } from './_framework/dotnet.js';

const { getAssemblyExports, getConfig } = await dotnet.create();
const exports = await getAssemblyExports(getConfig().mainAssemblyName);

globalThis.cufet = {
    run:   source => exports.Cufet.Playground.Runtime.Run(source),
    check: source => exports.Cufet.Playground.Runtime.Check(source),
};

// Prove it works without needing a page: run one program at boot and report the result.
const probe = 'Define greeting as "hello from WebAssembly".\nState greeting.\nState 6 * 7.';
console.log(globalThis.cufet.run(probe));
document.dispatchEvent(new CustomEvent('cufet-ready'));
