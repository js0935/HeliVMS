using HeliVMS.Models;

namespace HeliVMS.Services;

public interface IUserService {
    List<User> GetAllUsers();
    User? GetUserById(string id);
    User? GetUserByUsername(string username);
    User? ValidateUser(string username, string passwordHash);
    bool CreateUser(User user, string password);
    bool UpdateUser(User user);
    bool DeleteUser(string id);
    bool IsUsernameTaken(string username);
}
