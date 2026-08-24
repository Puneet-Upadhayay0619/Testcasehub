using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace TestCaseHub.Api.Services;

// Real production bug found via live testing (TC-UWMC-DSH-046 "Run" -> "Could not run script:
// Request failed (500)"): ASP.NET Core's Data Protection API, by default, persists its key ring
// to the local container filesystem. Render (like most container hosts) gives every deploy a
// brand-new, EPHEMERAL filesystem -- so the moment a new version is deployed, the old keys are
// gone and a fresh key ring is generated. Any secret that was Protect()-ed under the OLD ring
// (an EnvironmentCredential's Bearer token, an EnvironmentTarget's DB connection string --
// anything SecretProtector ever encrypted) becomes permanently un-Unprotect-able: it throws an
// uncaught CryptographicException, which is what surfaced to the user as a bare 500.
//
// Fix: store the key ring itself in the same Postgres database that already survives every
// redeploy, so keys -- and therefore every secret protected with them -- survive too. This
// mirrors the rest of this file's approach to schema (idempotent raw SQL, no EF migrations).
//
// IMPORTANT CONSEQUENCE: keys generated under the OLD (lost) ring cannot be recovered by this
// fix. Any credential/DB-connection-string saved BEFORE this deploy is still permanently
// undecryptable and must be re-entered once, after which it will persist correctly across every
// future redeploy.
public class PostgresXmlRepository : IXmlRepository
{
    private readonly string _connectionString;
    public PostgresXmlRepository(string connectionString) => _connectionString = connectionString;

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var elements = new List<XElement>();
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(@"SELECT ""Xml"" FROM ""DataProtectionKeys"" ORDER BY ""Id"";", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            elements.Add(XElement.Parse(reader.GetString(0)));
        return elements;
    }

    public void StoreElement(XElement element, string? friendlyName)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            @"INSERT INTO ""DataProtectionKeys"" (""FriendlyName"", ""Xml"") VALUES (@name, @xml);", conn);
        cmd.Parameters.AddWithValue("name", (object?)friendlyName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        cmd.ExecuteNonQuery();
    }
}
