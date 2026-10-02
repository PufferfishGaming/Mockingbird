using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TriAsr.Application;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary>The notes (ADR: notes): the files they are kept in, and <c>/v1/notes</c> on a server that keeps them for the computers that use it.</summary>
public sealed class NotesApiTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");

    private static string ErrorCode(string body) => JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    // ---- the files --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANoteIsAFileThatSurvivesTheProgramAndIsFoundAgain()
    {
        var root = NewRoot();
        try
        {
            var store = new FileNoteStore(root);
            var first = await store.CreateAsync("  Shopping  ", "Milk and bread.", default);
            Assert.Equal("Shopping", first.Title);                                           // the title is trimmed
            Assert.Equal(1, first.Revision);
            Assert.True(File.Exists(Path.Combine(root, "Notes", first.Id.ToString("N") + ".json")));
            var second = await store.CreateAsync("", "Call the dentist.", default);
            await Task.Delay(20);
            var saved = await store.SaveAsync(first.Id, "Shopping", "Milk, bread and eggs.", 1, default);
            Assert.Equal(2, saved.Revision);
            Assert.True(saved.UpdatedUtc > first.UpdatedUtc);
            Assert.Equal(first.CreatedUtc, saved.CreatedUtc);

            var again = new FileNoteStore(root);                                              // another run of the program
            var list = await again.ListAsync(default);
            Assert.Equal([first.Id, second.Id], list.Select(note => note.Id));                // the one changed last comes first
            Assert.Equal("Milk, bread and eggs.", (await again.GetAsync(first.Id, default))!.Text);
            Assert.Null(await again.GetAsync(Guid.NewGuid(), default));
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task ASaveFromAnOldRevisionIsRefusedAndNothingIsLost()
    {
        var root = NewRoot();
        try
        {
            var store = new FileNoteStore(root);
            var note = await store.CreateAsync("A", "one", default);
            var changed = await store.SaveAsync(note.Id, "A", "two", 1, default);              // written in one window
            var refused = await Assert.ThrowsAsync<NoteException>(() => store.SaveAsync(note.Id, "A", "three", 1, default));    // another window still looks at revision 1
            Assert.Equal(NoteMessages.Changed, refused.Message);
            Assert.Equal("two", (await store.GetAsync(note.Id, default))!.Text);
            Assert.Equal(changed.Revision, (await store.GetAsync(note.Id, default))!.Revision);
            var forced = await store.SaveAsync(note.Id, "A", "three", null, default);          // no revision named: whatever is there is replaced
            Assert.Equal(3, forced.Revision);
            Assert.Equal(forced, await store.SaveAsync(note.Id, "A", "three", 3, default));    // nothing changed: nothing is written, the revision stays
            var gone = await Assert.ThrowsAsync<NoteException>(() => store.SaveAsync(Guid.NewGuid(), "x", "y", null, default));
            Assert.Equal(NoteMessages.NotFound, gone.Message);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task ADeletedNoteIsGoneFromTheFolderAndATooLongOneIsRefused()
    {
        var root = NewRoot();
        try
        {
            var store = new FileNoteStore(root);
            var note = await store.CreateAsync("Gone soon", "text", default);
            Assert.True(await store.DeleteAsync(note.Id, default));
            Assert.False(await store.DeleteAsync(note.Id, default));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "Notes")));
            Assert.Empty(await new FileNoteStore(root).ListAsync(default));

            var tooLong = await Assert.ThrowsAsync<NoteException>(() => store.CreateAsync("", new string('x', NoteMessages.MaxText + 1), default));
            Assert.Equal(NoteMessages.TooLong, tooLong.Message);
            await Assert.ThrowsAsync<NoteException>(() => store.CreateAsync(new string('t', NoteMessages.MaxTitle + 1), "", default));
            var limit = await store.CreateAsync("", new string('x', NoteMessages.MaxText), default);          // exactly the limit is fine
            Assert.Equal(NoteMessages.MaxText, limit.Text.Length);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task AFileThatIsNotANoteIsLeftAloneAndSkipped()
    {
        var root = NewRoot();
        try
        {
            var store = new FileNoteStore(root);
            var good = await store.CreateAsync("Good", "kept", default);
            File.WriteAllText(Path.Combine(root, "Notes", "garbage.json"), "this is not json");
            File.WriteAllText(Path.Combine(root, "Notes", Guid.NewGuid().ToString("N") + ".json"), "{\"id\":\"00000000-0000-0000-0000-000000000000\",\"title\":\"x\"}");
            var list = await new FileNoteStore(root).ListAsync(default);
            Assert.Equal([good.Id], list.Select(note => note.Id));
            Assert.True(File.Exists(Path.Combine(root, "Notes", "garbage.json")));
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public void TheFirstWordsOfANoteAreOneLineThatEndsInDotsWhenThereIsMore()
    {
        Assert.Equal("", NoteText.Preview(""));
        Assert.Equal("", NoteText.Preview(" \r\n\t "));
        Assert.Equal("Milk and bread. Eggs.", NoteText.Preview("  Milk and\r\nbread.\n\n   Eggs.  "));
        Assert.Equal("abcde…", NoteText.Preview("abcdefghij", 5));
        Assert.Equal("abcde", NoteText.Preview("abcde", 5));                                  // exactly as long as the limit: nothing was cut
        Assert.Equal("abcde", NoteText.Preview("abcde   \n  ", 5));
        Assert.Equal("ab cd…", NoteText.Preview("ab   cd efgh", 5));
    }

    // ---- the API ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANoteIsCreatedListedReadSavedAndDeleted()
    {
        await using var api = await Harness.StartAsync(h => h.Notes = new FileNoteStore(NewRoot()));
        using var created = await api.Client.PostAsync("/v1/notes", Json(new { title = "Ideas", text = "First idea.\nSecond idea." }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var note = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var id = ApiTestData.Text(note, "id");
        Assert.Equal($"/v1/notes/{id}", created.Headers.Location!.OriginalString);
        Assert.Equal(1, note.GetProperty("revision").GetInt32());

        var list = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/notes")).RootElement.GetProperty("data");
        var row = Assert.Single(list.EnumerateArray());
        Assert.Equal("Ideas", ApiTestData.Text(row, "title"));
        Assert.Equal("First idea. Second idea.", ApiTestData.Text(row, "preview"));            // a list shows one line, not the whole text
        Assert.Equal(24, row.GetProperty("length").GetInt32());
        Assert.False(row.TryGetProperty("text", out _));

        var read = JsonDocument.Parse(await api.Client.GetStringAsync($"/v1/notes/{id}")).RootElement;
        Assert.Equal("First idea.\nSecond idea.", ApiTestData.Text(read, "text"));

        using var put = await api.Client.PutAsync($"/v1/notes/{id}", Json(new { title = "Ideas", text = "First idea.\nSecond idea.\nThird.", revision = 1 }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(2, JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement.GetProperty("revision").GetInt32());

        using var stale = await api.Client.PutAsync($"/v1/notes/{id}", Json(new { title = "Ideas", text = "overwritten", revision = 1 }));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("note_changed", ErrorCode(await stale.Content.ReadAsStringAsync()));
        Assert.EndsWith("Third.", ApiTestData.Text(JsonDocument.Parse(await api.Client.GetStringAsync($"/v1/notes/{id}")).RootElement, "text"));

        using var partial = await api.Client.PutAsync($"/v1/notes/{id}", Json(new { title = "Renamed", revision = 2 }));        // only the title: the text stays
        var renamed = JsonDocument.Parse(await partial.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Renamed", ApiTestData.Text(renamed, "title"));
        Assert.EndsWith("Third.", ApiTestData.Text(renamed, "text"));

        using var deleted = await api.Client.DeleteAsync($"/v1/notes/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using var again = await api.Client.DeleteAsync($"/v1/notes/{id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        using var read404 = await api.Client.GetAsync($"/v1/notes/{id}");
        Assert.Equal(HttpStatusCode.NotFound, read404.StatusCode);
        using var save404 = await api.Client.PutAsync($"/v1/notes/{id}", Json(new { title = "x", text = "y" }));
        Assert.Equal(HttpStatusCode.NotFound, save404.StatusCode);
    }

    [Fact]
    public async Task NotesNeedThePasswordAndABadRequestIsRefusedBeforeAnythingIsKept()
    {
        var store = new FileNoteStore(NewRoot());
        await using var api = await Harness.StartAsync(h => h.Notes = store);
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        using var refused = await anonymous.GetAsync("/v1/notes");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        using var refusedPost = await anonymous.PostAsync("/v1/notes", Json(new { title = "x", text = "y" }));
        Assert.Equal(HttpStatusCode.Unauthorized, refusedPost.StatusCode);

        using var notJson = await api.Client.PostAsync("/v1/notes", new StringContent("this is not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Assert.Equal("bad_json", ErrorCode(await notJson.Content.ReadAsStringAsync()));
        using var tooLong = await api.Client.PostAsync("/v1/notes", Json(new { title = "x", text = new string('y', NoteMessages.MaxText + 1) }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLong.StatusCode);
        using var wrongMethod = await api.Client.PatchAsync("/v1/notes", Json(new { }));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        using var badId = await api.Client.GetAsync("/v1/notes/not-an-id");
        Assert.Equal(HttpStatusCode.NotFound, badId.StatusCode);
        Assert.Empty(await store.ListAsync(default));
    }

    [Fact]
    public async Task AServerThatKeepsNoNotesSaysSoAndTheInfoTellsClientsInAdvance()
    {
        await using var plain = await Harness.StartAsync();
        using var none = await plain.Client.GetAsync("/v1/notes");
        Assert.Equal(HttpStatusCode.NotImplemented, none.StatusCode);
        Assert.Equal("notes_unavailable", ErrorCode(await none.Content.ReadAsStringAsync()));
        Assert.False(JsonDocument.Parse(await plain.Client.GetStringAsync("/v1/server")).RootElement.GetProperty("notesEnabled").GetBoolean());

        await using var api = await Harness.StartAsync(h => h.Notes = new FileNoteStore(NewRoot()));
        Assert.True(JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement.GetProperty("notesEnabled").GetBoolean());
    }

    [Fact]
    public async Task TheClientLibraryCarriesNotesAndTheirErrors()
    {
        await using var api = await Harness.StartAsync(h => h.Notes = new FileNoteStore(NewRoot()));
        using var client = new RemoteServerClient(api.Client.BaseAddress!, ApiTestData.Key, null);
        Assert.True((await client.InfoAsync(default)).NotesEnabled);
        Assert.Empty(await client.NotesAsync(default));

        var note = await client.CreateNoteAsync("Meeting", "Agenda.", default);
        Assert.Equal(1, note.Revision);
        var saved = await client.SaveNoteAsync(note.Id, "Meeting", "Agenda. Minutes.", note.Revision, default);
        Assert.Equal(2, saved.Revision);
        var summary = Assert.Single(await client.NotesAsync(default));
        Assert.Equal("Agenda. Minutes.", summary.Preview);
        Assert.Equal("Agenda. Minutes.", (await client.NoteAsync(note.Id, default)).Text);

        var stale = await Assert.ThrowsAsync<RemoteException>(() => client.SaveNoteAsync(note.Id, "Meeting", "late", note.Revision, default));
        Assert.Equal("note_changed", stale.Code);
        Assert.Equal(NoteMessages.Changed, stale.Message);                                    // the server's own sentence, so that the window can translate it

        await client.DeleteNoteAsync(note.Id, default);
        var gone = await Assert.ThrowsAsync<RemoteException>(() => client.NoteAsync(note.Id, default));
        Assert.Equal("not_found", gone.Code);
        Assert.Equal(NoteMessages.NotFound, gone.Message);

        using var wrong = new RemoteServerClient(api.Client.BaseAddress!, "wrong", null);
        Assert.True((await Assert.ThrowsAsync<RemoteException>(() => wrong.NotesAsync(default))).IsAuthentication);
    }
}
