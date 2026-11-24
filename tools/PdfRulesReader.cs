using System;
using System.IO;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

internal static class PdfRulesReader
{
    static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        try
        {
            using (var pdf = PdfDocument.Open(args[0]))
            {
                if (pdf.NumberOfPages > 80) return 3;
                var text = new StringBuilder();
                foreach (var page in pdf.GetPages())
                {
                    text.AppendLine(ContentOrderTextExtractor.GetText(page));
                    if (text.Length > 400000) return 3;
                }
                File.WriteAllText(args[1], text.ToString(), new UTF8Encoding(false));
            }
            return 0;
        }
        catch (Exception)
        {
            return 3;
        }
    }
}
