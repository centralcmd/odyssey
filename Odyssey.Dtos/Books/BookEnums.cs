namespace Odyssey.Dtos.Books;

/// <summary>A book's physical or digital format (issue #527 §8.2). Ordinals are a wire and persistence contract.</summary>
public enum BookFormat
{
    Hardcover = 0,
    Paperback = 1,
    PocketPaperback = 2,
    Audiobook = 3,
    Ebook = 4,
    Pdf = 5,
    Other = 6,
}

/// <summary>Whether the household has the book (issue #527 §8.2). Ordinals are a wire and persistence contract.</summary>
public enum BookStatus
{
    Owned = 0,
    LentOut = 1,
    Wishlist = 2,
    SoldOrGivenAway = 3,
}
