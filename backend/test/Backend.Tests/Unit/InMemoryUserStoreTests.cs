using System.Collections;
using System.Reflection;

using SeniorsInTheMiddle.Proxy.Auth.Domain;
using SeniorsInTheMiddle.Proxy.Auth.Storage;

namespace Backend.Tests.Unit;

[TestClass]
public class InMemoryUserStoreTests
{
    private const string Password = "correct horse";

    [TestMethod]
    public async Task VerifyPassword_ReturnsTheUser_ForTheRightPassword()
    {
        var store = new InMemoryUserStore();
        await store.SaveAsync(new User("ruth", "ruth@test.ch"), Password);

        User? user = await store.VerifyPassword("ruth", Password);

        Assert.AreEqual("ruth", user?.Username);
    }

    [TestMethod]
    public async Task VerifyPassword_ReturnsNull_ForTheWrongPassword()
    {
        var store = new InMemoryUserStore();
        await store.SaveAsync(new User("ruth", "ruth@test.ch"), Password);

        Assert.IsNull(await store.VerifyPassword("ruth", "wrong horse"));
    }

    [TestMethod]
    public async Task VerifyPassword_ReturnsNull_ForAnUnknownUser()
    {
        var store = new InMemoryUserStore();

        Assert.IsNull(await store.VerifyPassword("nobody", Password));
    }

    [TestMethod]
    public async Task TryCreateAddsAUserThatIsNotThereYet()
    {
        var store = new InMemoryUserStore();

        Assert.IsTrue(await store.TryCreateAsync(new User("ruth", "ruth@test.ch"), Password));
        Assert.IsNotNull(await store.VerifyPassword("ruth", Password));
    }

    [TestMethod]
    public async Task TryCreateRefusesATakenUsernameAndKeepsTheFirstPassword()
    {
        // The registration race, in the form it takes once the two callers have been
        // serialized: the second must be turned away rather than overwrite the first, whose
        // password would otherwise stop working with nothing to explain it.
        var store = new InMemoryUserStore();
        await store.TryCreateAsync(new User("ruth", "ruth@test.ch"), Password);

        Assert.IsFalse(await store.TryCreateAsync(new User("ruth", "other@test.ch"), "second password"));
        Assert.IsNotNull(await store.VerifyPassword("ruth", Password));
        Assert.IsNull(await store.VerifyPassword("ruth", "second password"));
    }

    [TestMethod]
    public async Task TryCreateRefusesATakenEmail()
    {
        var store = new InMemoryUserStore();
        await store.TryCreateAsync(new User("ruth", "shared@test.ch"), Password);

        Assert.IsFalse(await store.TryCreateAsync(new User("hans", "SHARED@test.ch"), Password));
        Assert.IsNull(await store.FindByUsernameAsync("hans"));
    }

    [TestMethod]
    public async Task ConcurrentTryCreatesOnOneNameProduceExactlyOneWinner()
    {
        // The race the endpoint used to lose. Without an atomic create every one of these
        // succeeds and the last one's password is the one that survives.
        var store = new InMemoryUserStore();

        bool[] results = await Task.WhenAll(Enumerable.Range(0, 32).Select(index =>
            Task.Run(() => store.TryCreateAsync(new User("ruth", $"ruth{index}@test.ch"), Password))));

        Assert.AreEqual(1, results.Count(created => created));
    }

    [TestMethod]
    public async Task SeededAndSavedUsersBothVerify()
    {
        var store = new InMemoryUserStore();
        store.Seed(new User("seeded", "seeded@test.ch"), Password);
        await store.SaveAsync(new User("saved", "saved@test.ch"), Password);

        Assert.IsNotNull(await store.VerifyPassword("seeded", Password));
        Assert.IsNotNull(await store.VerifyPassword("saved", Password));
    }

    [TestMethod]
    public async Task ThePasswordIsNotKeptAnywhereInTheStore()
    {
        // The store used to hold the password verbatim and compare with ==. This is the
        // assertion that keeps it from drifting back: nothing it retains may contain the
        // original text, whatever shape the entry takes.
        var store = new InMemoryUserStore();
        await store.SaveAsync(new User("ruth", "ruth@test.ch"), Password);

        foreach (string value in StoredStrings(store))
            StringAssert.DoesNotMatch(value, new System.Text.RegularExpressions.Regex(Password));
    }

    [TestMethod]
    public async Task TwoUsersWithTheSamePasswordGetDifferentHashes()
    {
        // Salting, observed rather than assumed: identical passwords that hash alike would
        // mean the salt is not reaching PBKDF2.
        var store = new InMemoryUserStore();
        await store.SaveAsync(new User("ruth", "ruth@test.ch"), Password);
        await store.SaveAsync(new User("hans", "hans@test.ch"), Password);

        List<string> stored = [.. StoredStrings(store)];

        CollectionAssert.AllItemsAreUnique(stored);
    }

    /// <summary>
    /// Every string the store is holding onto, reached through the private dictionary. White
    /// box on purpose: the point is what is left in memory, which no public member exposes.
    /// </summary>
    private static IEnumerable<string> StoredStrings(InMemoryUserStore store)
    {
        FieldInfo field = typeof(InMemoryUserStore)
            .GetField("_users", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("InMemoryUserStore._users is gone; update this test.");

        var entries = (IEnumerable)(field.GetValue(store)
            ?? throw new InvalidOperationException("InMemoryUserStore._users was null."));

        foreach (object? pair in entries)
        {
            object? value = pair?.GetType().GetProperty("Value")?.GetValue(pair);
            if (value is null)
                continue;

            foreach (PropertyInfo property in value.GetType().GetProperties())
            {
                if (property.GetValue(value) is string text)
                    yield return text;
            }
        }
    }
}
