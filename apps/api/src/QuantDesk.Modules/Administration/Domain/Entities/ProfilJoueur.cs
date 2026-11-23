namespace QuantDesk.Modules.Administration.Domain.Entities;

/// <summary>Optional business identity attached to an authenticated player, not an auth credential.</summary>
public sealed class ProfilJoueur
{
    public Guid Id { get; private set; }
    public Guid UtilisateurId { get; private set; }
    public string PseudoPublic { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    private ProfilJoueur() { }

    public static ProfilJoueur Create(Guid utilisateurId, string pseudoPublic)
    {
        if (utilisateurId == Guid.Empty) throw new ArgumentException("User ID required", nameof(utilisateurId));
        return new ProfilJoueur
        {
            Id = Guid.NewGuid(), UtilisateurId = utilisateurId,
            PseudoPublic = ValidatePseudo(pseudoPublic), CreatedAtUtc = DateTime.UtcNow
        };
    }

    public void Rename(string pseudoPublic) => PseudoPublic = ValidatePseudo(pseudoPublic);

    private static string ValidatePseudo(string input)
    {
        var clean = input?.Trim() ?? "";
        if (clean.Length is < 3 or > 32 ||
            !clean.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
            throw new ArgumentException("Public name must be 3-32 characters: letters, digits, _, -, .");
        return clean;
    }
}
