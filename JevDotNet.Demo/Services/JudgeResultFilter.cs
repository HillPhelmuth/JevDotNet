namespace JevDotNet.Demo.Services;

public static class JudgeResultFilter
{
    public static bool DiffersFromExpected(int expectedScore, int? decisionRating, int? lunaRating = null) =>
        (decisionRating is int decision && decision != expectedScore) ||
        (lunaRating is int luna && luna != expectedScore);
}
