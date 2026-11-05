// Self-contained check of the Media Manager API. It hosts the plugin's real controller in a tiny
// ASP.NET Core app with a fake ILibraryManager and always-authenticated admin user, then drives it over HTTP.
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jellyfin.Plugin.MediaManager;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

var work = Path.Combine(Path.GetTempPath(), "mm-apicheck-" + Guid.NewGuid().ToString("N")[..8]);
var libRoot = Path.Combine(work, "movies");
var outside = Path.Combine(work, "outside");
Directory.CreateDirectory(libRoot);
Directory.CreateDirectory(outside);
File.WriteAllText(Path.Combine(outside, "secret.txt"), "top secret");
Directory.CreateSymbolicLink(Path.Combine(libRoot, "escape"), outside);

FakeLibrary.Root = libRoot;
var library = DispatchProxy.Create<ILibraryManager, FakeLibrary>();

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton(library);
builder.Services.AddControllers().AddApplicationPart(typeof(Plugin).Assembly);
builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuth>("test", null);
builder.Services.AddAuthorization(o => o.AddPolicy("RequiresElevation", p => p.RequireAuthenticatedUser()));
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
await app.StartAsync();

var baseUrl = app.Urls.First();
using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
int pass = 0, fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"PASS  {name}"); }
    else { fail++; Console.WriteLine($"FAIL  {name}  {detail}"); }
}

static string Q(string s) => Uri.EscapeDataString(s);
async Task<(HttpStatusCode code, JsonElement json)> Send(HttpMethod m, string url, HttpContent? body = null)
{
    var res = await http.SendAsync(new HttpRequestMessage(m, url) { Content = body });
    var text = await res.Content.ReadAsStringAsync();
    JsonElement el = default;
    if (!string.IsNullOrWhiteSpace(text)) { try { el = JsonDocument.Parse(text).RootElement.Clone(); } catch { } }
    return (res.StatusCode, el);
}
Task<(HttpStatusCode, JsonElement)> Json(string url, object body) => Send(HttpMethod.Post, url, JsonContent.Create(body));
Task<(HttpStatusCode, JsonElement)> Chunk(string dir, string name, long offset, long total, byte[] data, bool overwrite = false) =>
    Send(HttpMethod.Post, $"/MediaManager/Upload?path={Q(dir)}&name={Q(name)}&offset={offset}&total={total}&overwrite={overwrite.ToString().ToLower()}",
        new ByteArrayContent(data));

// ---- info + browse
var (c, j) = await Send(HttpMethod.Get, "/MediaManager/Info");
Check("Info returns the library and settings", c == HttpStatusCode.OK && j.GetProperty("libraries").GetArrayLength() == 1
    && j.GetProperty("settings").GetProperty("allowedExtensions").GetArrayLength() > 5);

(c, j) = await Send(HttpMethod.Get, $"/MediaManager/Browse?path={Q(libRoot)}");
Check("Browse of the library root works and hides the symlink", c == HttpStatusCode.OK
    && j.GetProperty("entries").EnumerateArray().All(e => e.GetProperty("name").GetString() != "escape"), c.ToString());

(c, _) = await Send(HttpMethod.Get, $"/MediaManager/Browse?path={Q(outside)}");
Check("Browse outside the library is refused", c == HttpStatusCode.BadRequest, c.ToString());

(c, _) = await Send(HttpMethod.Get, $"/MediaManager/Browse?path={Q(libRoot + "/../outside")}");
Check("Browse with ../ traversal is refused", c == HttpStatusCode.BadRequest, c.ToString());

(c, _) = await Send(HttpMethod.Get, $"/MediaManager/Browse?path={Q(Path.Combine(libRoot, "escape"))}");
Check("Browse through a symlink is refused", c == HttpStatusCode.BadRequest, c.ToString());

// ---- upload: multi-chunk, byte-exact
var payload = new byte[2_500_000];
RandomNumberGenerator.Fill(payload);
const int cs = 1_000_000;
bool complete = false;
for (long off = 0; off < payload.Length; off += cs)
{
    var part = payload.Skip((int)off).Take(cs).ToArray();
    (c, j) = await Chunk(libRoot, "Test Film (2024).mkv", off, payload.Length, part);
    if (c != HttpStatusCode.OK) { break; }
    complete = j.GetProperty("complete").GetBoolean();
}
var finalPath = Path.Combine(libRoot, "Test Film (2024).mkv");
Check("3-chunk upload completes", complete);
Check("Uploaded file is byte-identical", File.Exists(finalPath) && SHA256.HashData(File.ReadAllBytes(finalPath)).SequenceEqual(SHA256.HashData(payload)));
Check("No .mmpart left behind", !File.Exists(finalPath + ".mmpart"));

// ---- resume + out-of-step handling
var big = new byte[1_500_000];
RandomNumberGenerator.Fill(big);
(c, j) = await Chunk(libRoot, "resume.mkv", 0, big.Length, big.Take(cs).ToArray());
Check("First chunk accepted, not complete", c == HttpStatusCode.OK && !j.GetProperty("complete").GetBoolean());
(c, j) = await Chunk(libRoot, "resume.mkv", 1_200_000, big.Length, big.Skip(1_200_000).ToArray());
Check("Out-of-step chunk gets 409 with expected offset", c == HttpStatusCode.Conflict && j.GetProperty("expected").GetInt64() == cs, $"{c}");
(c, j) = await Chunk(libRoot, "resume.mkv", cs, big.Length, big.Skip(cs).ToArray());
Check("Resuming at the expected offset completes", c == HttpStatusCode.OK && j.GetProperty("complete").GetBoolean());
Check("Resumed file is byte-identical", SHA256.HashData(File.ReadAllBytes(Path.Combine(libRoot, "resume.mkv"))).SequenceEqual(SHA256.HashData(big)));

// ---- exists / overwrite
(c, j) = await Chunk(libRoot, "resume.mkv", 0, 10, new byte[10]);
Check("Existing file without overwrite gets code 'exists'", c == HttpStatusCode.Conflict && j.GetProperty("code").GetString() == "exists", c.ToString());
(c, j) = await Chunk(libRoot, "resume.mkv", 0, 10, new byte[10], overwrite: true);
Check("Overwrite=true replaces it", c == HttpStatusCode.OK && new FileInfo(Path.Combine(libRoot, "resume.mkv")).Length == 10);

// ---- rejected names / types
(c, _) = await Chunk(libRoot, "payload.exe", 0, 10, new byte[10]);
Check(".exe upload refused", c == HttpStatusCode.BadRequest, c.ToString());
(c, _) = await Chunk(libRoot, "../evil.mkv", 0, 10, new byte[10]);
Check("Name with ../ refused", c == HttpStatusCode.BadRequest, c.ToString());
(c, _) = await Chunk(libRoot, "x.mkv.mmpart", 0, 10, new byte[10]);
Check("Reserved .mmpart name refused", c == HttpStatusCode.BadRequest, c.ToString());
(c, _) = await Chunk(outside, "a.mkv", 0, 10, new byte[10]);
Check("Upload outside the library refused", c == HttpStatusCode.BadRequest && !File.Exists(Path.Combine(outside, "a.mkv")), c.ToString());
(c, _) = await Chunk(Path.Combine(libRoot, "escape"), "a.mkv", 0, 10, new byte[10]);
Check("Upload through a symlink refused", c == HttpStatusCode.BadRequest && !File.Exists(Path.Combine(outside, "a.mkv")), c.ToString());

// ---- cancel
await Chunk(libRoot, "half.mkv", 0, 5_000_000, new byte[cs]);
(c, _) = await Send(HttpMethod.Delete, $"/MediaManager/Upload?path={Q(libRoot)}&name={Q("half.mkv")}");
Check("Cancel removes the unfinished upload", c == HttpStatusCode.NoContent && !File.Exists(Path.Combine(libRoot, "half.mkv.mmpart")), c.ToString());

// ---- folders, rename, move, delete
(c, _) = await Json("/MediaManager/Folder", new { path = libRoot, name = "Season 1" });
Check("Create folder", c == HttpStatusCode.OK && Directory.Exists(Path.Combine(libRoot, "Season 1")), c.ToString());
(c, _) = await Json("/MediaManager/Folder", new { path = libRoot, name = "Season 1" });
Check("Duplicate folder gets 409", c == HttpStatusCode.Conflict, c.ToString());

(c, _) = await Json("/MediaManager/Rename", new { path = finalPath, newName = "Renamed Film (2024).mkv" });
Check("Rename a file", c == HttpStatusCode.OK && File.Exists(Path.Combine(libRoot, "Renamed Film (2024).mkv")), c.ToString());
(c, _) = await Json("/MediaManager/Rename", new { path = Path.Combine(libRoot, "Renamed Film (2024).mkv"), newName = "run.exe" });
Check("Rename to a disallowed extension refused", c == HttpStatusCode.BadRequest, c.ToString());
(c, _) = await Json("/MediaManager/Rename", new { path = libRoot, newName = "elsewhere" });
Check("Renaming the library root refused", c == HttpStatusCode.BadRequest, c.ToString());

(c, _) = await Json("/MediaManager/Move", new { path = Path.Combine(libRoot, "Renamed Film (2024).mkv"), destination = Path.Combine(libRoot, "Season 1") });
Check("Move a file into a folder", c == HttpStatusCode.OK && File.Exists(Path.Combine(libRoot, "Season 1", "Renamed Film (2024).mkv")), c.ToString());
(c, _) = await Json("/MediaManager/Move", new { path = Path.Combine(libRoot, "Season 1"), destination = Path.Combine(libRoot, "Season 1") });
Check("Moving a folder into itself refused", c == HttpStatusCode.BadRequest, c.ToString());
(c, _) = await Json("/MediaManager/Move", new { path = Path.Combine(libRoot, "Season 1", "Renamed Film (2024).mkv"), destination = outside });
Check("Move to a destination outside the library refused", c == HttpStatusCode.BadRequest, c.ToString());

(c, j) = await Json("/MediaManager/Delete", new { path = Path.Combine(libRoot, "Season 1"), recursive = false });
Check("Delete non-empty folder without recursive gets 409", c == HttpStatusCode.Conflict && j.GetProperty("code").GetString() == "not-empty", c.ToString());
(c, _) = await Json("/MediaManager/Delete", new { path = Path.Combine(libRoot, "Season 1"), recursive = true });
Check("Recursive delete works", c == HttpStatusCode.NoContent && !Directory.Exists(Path.Combine(libRoot, "Season 1")), c.ToString());
(c, _) = await Json("/MediaManager/Delete", new { path = libRoot, recursive = true });
Check("Deleting the library root refused", c == HttpStatusCode.BadRequest && Directory.Exists(libRoot), c.ToString());
(c, _) = await Json("/MediaManager/Delete", new { path = Path.Combine(outside, "secret.txt"), recursive = false });
Check("Deleting a file outside the library refused", c == HttpStatusCode.BadRequest && File.Exists(Path.Combine(outside, "secret.txt")), c.ToString());

// ---- scan
(c, _) = await Send(HttpMethod.Post, "/MediaManager/Scan");
Check("Scan queues a library scan", c == HttpStatusCode.NoContent && FakeLibrary.ScanQueued);

await app.StopAsync();
try { Directory.Delete(work, true); } catch { }
Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;

public class FakeLibrary : DispatchProxy
{
    public static string Root = "";
    public static bool ScanQueued;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case "GetVirtualFolders":
                return new List<VirtualFolderInfo> { new() { Name = "Movies", Locations = new[] { Root } } };
            case "QueueLibraryScan":
                ScanQueued = true;
                return null;
            default:
                throw new NotSupportedException(targetMethod?.Name);
        }
    }
}

public class TestAuth : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : base(o, l, e) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = new ClaimsIdentity(new[] { new Claim("Jellyfin-User", "tester") }, "test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), "test")));
    }
}
