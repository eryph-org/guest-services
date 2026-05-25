using AwesomeAssertions;
using LanguageExt;
using LanguageExt.Common;

namespace Eryph.GuestServices.CloudConfig.Tests;

internal static class ValidationAssertions
{
    public static T ShouldBeSuccess<T>(this Validation<Error, T> validation)
    {
        var errors = validation.FailToSeq();
        errors.Should().BeEmpty(
            "expected a successful validation but got errors: {0}",
            string.Join("; ", errors.Map(e => e.Message)));
        return validation.SuccessToSeq().Head;
    }

    public static IReadOnlyList<Error> ShouldBeFail<T>(this Validation<Error, T> validation)
    {
        validation.IsFail.Should().BeTrue("expected a failed validation but it succeeded");
        return validation.FailToSeq().ToList();
    }

    public static IReadOnlyList<Error> Flatten(this IEnumerable<Error> errors)
    {
        var list = new List<Error>();
        foreach (var error in errors)
            Flatten(error, list);
        return list;
    }

    private static void Flatten(Error error, List<Error> sink)
    {
        if (error is ManyErrors many)
        {
            foreach (var inner in many.Errors)
                Flatten(inner, sink);
            return;
        }
        sink.Add(error);
    }
}
