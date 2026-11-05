namespace Shiro
{
    public class AccountJson
    {
        public long UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string UserEmail { get; set; } = string.Empty;
        public bool IsEmailVerified { get; set; }
        public int AgeBracket { get; set; }
        public bool UserAbove13 { get; set; }
    }
}
