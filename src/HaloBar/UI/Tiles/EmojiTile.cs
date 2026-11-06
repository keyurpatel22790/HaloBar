using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Tiles;

/// <summary>
/// A searchable emoji picker: type to find one ("fire", "heart"), or browse by category. Click an
/// emoji to copy it. The ones you use most show first under "Recent". Fully offline.
/// </summary>
internal sealed class EmojiTile : Tile
{
    private readonly TextField _search = new("Search emoji…", _ => { });
    private int _cat;

    public EmojiTile(Hub hub) : base(hub) { }

    public override string Id => "emoji";
    public override string Name => "Emoji";
    public override bool WideByDefault => true;
    public override double FrameInterval => 1;

    private sealed record Category(string Chip, string Name, (string E, string Kw)[] Items);

    // A curated set of common emoji with search keywords. Not exhaustive — the handy everyday ones.
    private static readonly Category[] Categories =
    [
        new("😀", "Smileys",
        [
            ("😀","grin happy smile"), ("😁","beam grin happy"), ("😂","joy laugh tears lol"), ("🤣","rofl laugh"),
            ("😊","smile blush happy"), ("🙂","slight smile"), ("😉","wink"), ("😍","love eyes heart"),
            ("🥰","love hearts adore"), ("😘","kiss"), ("😎","cool sunglasses"), ("🤩","star struck wow"),
            ("🤔","thinking hmm"), ("😏","smirk"), ("😴","sleep tired"), ("😭","cry sob sad"),
            ("😢","cry sad tear"), ("😤","huff proud"), ("😡","angry mad rage"), ("🥳","party celebrate"),
            ("😇","angel innocent"), ("🙃","upside down"), ("😜","wink tongue silly"), ("🤯","mind blown shock"),
            ("😳","flushed shock"), ("🥺","pleading puppy eyes"), ("😬","grimace awkward"), ("🤗","hug"),
            ("🤐","zip mouth quiet"), ("😷","mask sick"), ("🤒","sick fever"), ("🤮","vomit sick"),
        ]),
        new("👍", "Gestures",
        [
            ("👍","thumbs up yes like good"), ("👎","thumbs down no bad"), ("👌","ok perfect"), ("✌️","peace victory"),
            ("🤞","fingers crossed luck"), ("🙏","please thanks pray"), ("👏","clap applause"), ("🙌","raise hands praise"),
            ("💪","muscle strong"), ("🤝","handshake deal"), ("👋","wave hi hello bye"), ("✋","hand stop"),
            ("🤙","call me shaka"), ("👇","point down"), ("👆","point up"), ("👉","point right"),
            ("👈","point left"), ("🫶","heart hands love"), ("🤌","pinch italian"), ("✍️","writing hand"),
        ]),
        new("❤️", "Love",
        [
            ("❤️","red heart love"), ("🧡","orange heart"), ("💛","yellow heart"), ("💚","green heart"),
            ("💙","blue heart"), ("💜","purple heart"), ("🖤","black heart"), ("🤍","white heart"),
            ("💖","sparkle heart"), ("💕","two hearts love"), ("💔","broken heart"), ("❣️","heart exclamation"),
            ("💞","revolving hearts"), ("💓","beating heart"), ("💗","growing heart"), ("💘","heart arrow cupid"),
            ("😻","cat heart eyes"), ("💋","kiss lips"), ("🌹","rose flower"), ("💐","bouquet flowers"),
        ]),
        new("🐶", "Animals",
        [
            ("🐶","dog puppy"), ("🐱","cat kitten"), ("🐭","mouse"), ("🐹","hamster"), ("🐰","rabbit bunny"),
            ("🦊","fox"), ("🐻","bear"), ("🐼","panda"), ("🐨","koala"), ("🐯","tiger"),
            ("🦁","lion"), ("🐮","cow"), ("🐷","pig"), ("🐸","frog"), ("🐵","monkey"),
            ("🐔","chicken"), ("🐧","penguin"), ("🐦","bird"), ("🦄","unicorn"), ("🐝","bee"),
            ("🦋","butterfly"), ("🐢","turtle"), ("🐍","snake"), ("🐙","octopus"), ("🐠","fish"),
            ("🐳","whale"), ("🦅","eagle"), ("🦉","owl"), ("🐴","horse"), ("🐺","wolf"),
        ]),
        new("🍕", "Food",
        [
            ("🍕","pizza"), ("🍔","burger"), ("🍟","fries chips"), ("🌭","hotdog"), ("🍿","popcorn"),
            ("🥪","sandwich"), ("🌮","taco"), ("🍜","noodles ramen"), ("🍣","sushi"), ("🍦","icecream"),
            ("🍩","donut"), ("🍪","cookie"), ("🎂","cake birthday"), ("🍰","cake slice"), ("🍫","chocolate"),
            ("🍎","apple fruit"), ("🍌","banana"), ("🍓","strawberry"), ("🍇","grapes"), ("🍉","watermelon"),
            ("☕","coffee tea"), ("🍵","tea"), ("🍺","beer"), ("🍷","wine"), ("🥤","drink soda"),
        ]),
        new("⚽", "Activity",
        [
            ("⚽","football soccer"), ("🏀","basketball"), ("🏈","football american"), ("⚾","baseball"), ("🎾","tennis"),
            ("🏐","volleyball"), ("🏓","table tennis ping pong"), ("🏸","badminton"), ("🥊","boxing"), ("🏆","trophy win"),
            ("🥇","gold medal first"), ("🎮","game controller"), ("🎲","dice game"), ("🎯","target dart bullseye"), ("🎧","headphones music"),
            ("🎸","guitar music"), ("🎹","piano music"), ("🎤","mic sing"), ("🎬","movie clapper film"), ("🎨","art paint"),
        ]),
        new("✈️", "Travel",
        [
            ("✈️","plane flight travel"), ("🚗","car"), ("🚕","taxi"), ("🚌","bus"), ("🚲","bike bicycle"),
            ("🏍️","motorcycle"), ("🚆","train"), ("🚀","rocket launch"), ("🛸","ufo"), ("⛵","boat sail"),
            ("🚢","ship"), ("🗺️","map"), ("🧭","compass"), ("🏖️","beach"), ("🏔️","mountain"),
            ("🌋","volcano"), ("🗽","statue liberty"), ("🎡","ferris wheel"), ("🏠","house home"), ("🏢","office building"),
        ]),
        new("💡", "Objects",
        [
            ("💡","idea bulb light"), ("🔥","fire lit hot"), ("⭐","star"), ("✨","sparkles"), ("🎉","party tada celebrate"),
            ("🎁","gift present"), ("💰","money bag"), ("💵","dollar cash money"), ("💳","card pay"), ("📱","phone mobile"),
            ("💻","laptop computer"), ("⌨️","keyboard"), ("🖥️","desktop monitor"), ("📷","camera photo"), ("🔋","battery"),
            ("🔑","key"), ("🔒","lock secure"), ("📌","pin"), ("📎","clip paperclip"), ("✏️","pencil write"),
            ("📝","note memo"), ("📅","calendar date"), ("⏰","alarm clock time"), ("💊","pill medicine"), ("🧩","puzzle piece"),
        ]),
        new("✅", "Symbols",
        [
            ("✅","check done yes tick"), ("❌","cross no wrong"), ("❗","exclamation important"), ("❓","question"), ("⚠️","warning caution"),
            ("💯","hundred perfect score"), ("🔔","bell notify"), ("🔕","mute bell off"), ("♻️","recycle"), ("➡️","arrow right"),
            ("⬅️","arrow left"), ("⬆️","arrow up"), ("⬇️","arrow down"), ("🔄","refresh repeat"), ("➕","plus add"),
            ("➖","minus"), ("✔️","check tick"), ("🆗","ok"), ("🆕","new"), ("🚫","no ban forbidden"),
        ]),
    ];

    private static readonly (string E, string Kw)[] All = Categories.SelectMany(c => c.Items).DistinctBy(i => i.E).ToArray();

    private IEnumerable<string> Recents() =>
        Settings.EmojiRecent.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct();

    private void Pick(string emoji)
    {
        if (!Hub.Clipboard.CopyText(emoji)) return;
        Hub.ShowHud(new Hud(HudIcon.Copied, $"{emoji}  copied"));
        var list = new List<string> { emoji };
        list.AddRange(Recents().Where(e => e != emoji));
        Settings.EmojiRecent = string.Join(' ', list.Take(16));
        Settings.Save();
        Changed();
    }

    public override void Draw(Ui ui, SKRect t)
    {
        var wide = t.Width > 200;
        ui.Field(_search, new SKRect(t.Left + 8, t.Top + 8, t.Right - 8, t.Top + 30), 11.5f);
        var query = _search.Text.Trim().ToLowerInvariant();

        float gridTop;
        List<string> emojis;
        if (query.Length > 0)
        {
            emojis = All.Where(i => i.Kw.Contains(query) || i.E == query).Select(i => i.E).ToList();
            gridTop = t.Top + 36;
        }
        else
        {
            // Category chips: "Recent" first when there's anything there.
            var recents = Recents().ToList();
            float cx = t.Left + 8;
            var chipY = t.Top + 36;
            void CatChip(string label, int index)
            {
                var r = ui.Chip(cx, chipY, label, () => _cat = index, on: _cat == index, size: 12);
                cx = r.Right + 4;
            }
            if (recents.Count > 0) CatChip("★", -1);
            else if (_cat == -1) _cat = 0;
            for (var i = 0; i < Categories.Length; i++)
            {
                if (cx > t.Right - 30) break; // only as many chips as fit on the row
                CatChip(Categories[i].Chip, i);
            }
            emojis = _cat == -1 ? recents : Categories[_cat].Items.Select(i => i.E).ToList();
            gridTop = chipY + 22;
        }

        // The grid: as many emoji as fit, no scrolling.
        const float cell = 22f;
        var cols = Math.Max(1, (int)((t.Width - 12) / cell));
        var x0 = t.Left + 6 + (t.Width - 12 - cols * cell) / 2;
        var maxRows = Math.Max(1, (int)((t.Bottom - gridTop) / cell));
        for (var i = 0; i < emojis.Count && i < cols * maxRows; i++)
        {
            var col = i % cols;
            var row = i / cols;
            var r = new SKRect(x0 + col * cell, gridTop + row * cell, x0 + col * cell + cell, gridTop + row * cell + cell);
            if (ui.Hovering(r)) ui.Fill(SKRect.Inflate(r, -1, -1), 6, Theme.Overlay(30));
            ui.Text(emojis[i], r.MidX, r.MidY + 6, cell, 17, Theme.Weight.Regular, Theme.Text, SKTextAlign.Center);
            var chosen = emojis[i];
            ui.Hit(r, () => Pick(chosen));
        }
        if (emojis.Count == 0)
            ui.Text(query.Length > 0 ? "No emoji found" : "Nothing yet", t.MidX, gridTop + 20, t.Width - 16, 11, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
    }
}
