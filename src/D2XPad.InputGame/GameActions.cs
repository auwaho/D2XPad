namespace D2XPad.InputGame;

// Cumulative game actions since launch; Fire counts attempts, and Score counts hits.
internal readonly record struct GameActions(int Fire, int Recall, int PreviousColor, int NextColor);
