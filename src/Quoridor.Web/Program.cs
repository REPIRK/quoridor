using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Quoridor.Ui;
using Quoridor.Ui.Game;

// Where the browser's limits are declared, and the only place they are. The components
// below are the same ones a native shell hosts, and not one of them asks which host it is
// in: they ask the profile how long the engine may think and whether it may think in the
// player's time, and this line is the answer. WebAssembly here has a single thread and the
// search is on it, so both answers are smaller than a phone's — a three-second search
// would be three seconds of a page that does not repaint, scroll or accept a click.
HostProfile.Current = HostProfile.SingleThreaded;

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

await builder.Build().RunAsync();
