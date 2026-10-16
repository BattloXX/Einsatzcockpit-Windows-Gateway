namespace Einsatzcockpit.PrintAgent.Security;

public interface ITokenStore
{
    string? Read();
    void Save(string token);
    void Delete();
}
