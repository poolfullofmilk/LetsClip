using System.Diagnostics;
using LetsClip.Data.ViewModels;

namespace LetsClip.Data.Helpers;

public static class CaptionCards
{
    // A Few Short Words, One Line Even On Phones
    private const int MostWords = 3;
    private const int MostLetters = 18;

    // Long Pauses, Commas And Sentence Ends Start A Card
    private const double Pause = 0.5;

    // A Card Stays A Moment After Its Last Word
    private const double Hold = 0.4;

    static CaptionCards()
    {
        Debug.Assert(SelfTestPasses());
    }

    public static List<CaptionFrame> Of(IReadOnlyList<SpokenWord> words, TrimRange range)
    {
        // Words Of The Selection, Timed From Its Start
        var length = range.Length;
        var shown = words
            .Where(word => word.End > range.Start && word.Start < range.End)
            .Select(word =>
                (
                    Start: Math.Clamp(word.Start - range.Start, 0, length),
                    End: Math.Clamp(word.End - range.Start, 0, length),
                    word.Text
                )
            )
            .OrderBy(word => word.Start)
            .ToList();

        // Phrases Break On Pauses, Commas Or Sentence Ends
        List<List<(double Start, double End, string Text)>> phrases = [];
        foreach (var word in shown)
        {
            var phrase = phrases.Count > 0 ? phrases[^1] : null;
            if (
                phrase is null
                || word.Start - phrase[^1].End > Pause
                || phrase[^1].Text[^1] is '.' or '!' or '?' or ','
            )
            {
                phrases.Add([word]);
                continue;
            }

            phrase.Add(word);
        }

        var cards = phrases.SelectMany(CardsOf).ToList();

        // Each Word Shows When Said, Cards Hold A Moment
        List<CaptionFrame> frames = [];
        for (var index = 0; index < cards.Count; index++)
        {
            var card = cards[index];
            var next = index + 1 < cards.Count ? cards[index + 1][0].Start : length;
            var end = Math.Min(card[^1].End + Hold, next);
            string[] texts = [.. card.Select(part => part.Text)];
            for (var spoken = 0; spoken < card.Count; spoken++)
            {
                var from = Math.Max(card[spoken].Start, frames.Count > 0 ? frames[^1].End : 0);
                var to = spoken + 1 < card.Count ? card[spoken + 1].Start : end;
                if (to - from > 0.001)
                {
                    frames.Add(new CaptionFrame(from, to, texts, spoken + 1));
                }
            }
        }

        return frames;
    }

    private static List<List<(double Start, double End, string Text)>> CardsOf(
        List<(double Start, double End, string Text)> phrase
    )
    {
        // The Fewest Cards That Fit, Longer Ones First
        for (var count = (phrase.Count + MostWords - 1) / MostWords; ; count++)
        {
            int Edge(int index) => (index * phrase.Count + count - 1) / count;
            var cards = Enumerable
                .Range(0, count)
                .Select(index => phrase[Edge(index)..Edge(index + 1)])
                .ToList();
            if (
                count >= phrase.Count
                || cards.All(card => card.Sum(part => part.Text.Length + 1) - 1 <= MostLetters)
            )
            {
                return cards;
            }
        }
    }

    private static bool SelfTestPasses()
    {
        // Three Words, A Pause, Then A Sentence Of Two
        SpokenWord[] words =
        [
            new(1.0, 1.3, "Nice"),
            new(1.3, 1.6, "save"),
            new(1.6, 2.0, "there."),
            new(3.0, 3.4, "Again"),
            new(3.4, 3.8, "please"),
        ];
        var frames = Of(words, new TrimRange(0.5, 10));
        var trimmed = Of(words, new TrimRange(3.2, 3.6));

        // Four Words Read Two And Two, Never Three And One
        var phrase = Of(
            [
                new(0, 0.3, "watch"),
                new(0.3, 0.6, "the"),
                new(0.6, 0.9, "boost"),
                new(0.9, 1.2, "pads,"),
            ],
            new TrimRange(0, 5)
        );
        return phrase[0].Words is ["watch", "the"]
            && phrase[^1].Words is ["boost", "pads,"]
            && frames.Count == 5
            && frames[0] is { Start: 0.5, Spoken: 1, Words.Length: 3 }
            && Math.Abs(frames[2].End - 1.9) < 1e-9
            && frames[3].Words is ["Again", "please"]
            && Math.Abs(frames[4].End - 3.7) < 1e-9
            && trimmed.Count == 2
            && trimmed[0].Start == 0
            && Math.Abs(trimmed[^1].End - 0.4) < 1e-9;
    }
}
