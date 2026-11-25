using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.Linq;
using System.Text;

// LogHX stores its journal in a Jet database. The Windows Jet provider is 32 bit.
static class LogHxReader
{
    static readonly string[] Required = { "Call", "BandADIF", "Mode", "DateQSO", "TimeQSO" };

    static int Main()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            string file = Environment.GetEnvironmentVariable("AUTOCQ_LOGBOOK_SNAPSHOT");
            string preferred = Environment.GetEnvironmentVariable("AUTOCQ_LOGBOOK_TABLE");
            if (String.IsNullOrEmpty(file)) return 1;
            var settings = new OleDbConnectionStringBuilder();
            settings.Provider = "Microsoft.Jet.OLEDB.4.0";
            settings.DataSource = file;
            settings["Mode"] = "Read";
            using (var db = new OleDbConnection(settings.ConnectionString))
            {
                db.Open();
                WriteRows(db, preferred);
            }
            return 0;
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine("Для чтения LogHX нужен 32-разрядный поставщик Microsoft Jet. Можно выбрать экспорт журнала в ADIF.");
        }
        catch (OleDbException)
        {
            Console.Error.WriteLine("Не удалось прочитать файл LogHX. Проверьте выбранный журнал или используйте экспорт в ADIF.");
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
        }
        return 1;
    }

    static void WriteRows(OleDbConnection db, string preferred)
    {
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow row in db.GetSchema("Columns").Rows)
        {
            string table = row["TABLE_NAME"].ToString();
            HashSet<string> columns;
            if (!tables.TryGetValue(table, out columns))
                tables.Add(table, columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            columns.Add(row["COLUMN_NAME"].ToString());
        }
        var candidates = tables.Where(table => Required.All(table.Value.Contains)).ToArray();
        if (candidates.Length == 0) throw new ArgumentException("В выбранном файле нет таблицы радиосвязей LogHX.");
        string selected = candidates.Select(candidate => candidate.Key).FirstOrDefault(name =>
            String.Equals(name, preferred, StringComparison.OrdinalIgnoreCase));
        if (selected == null)
        {
            if (candidates.Length > 1) throw new ArgumentException("В файле несколько журналов. Экспортируйте нужный из LogHX в ADIF.");
            selected = candidates[0].Key;
        }
        if (selected.IndexOfAny(new[] { '[', ']', '\0' }) >= 0)
            throw new ArgumentException("Имя таблицы журнала нельзя прочитать. Выберите экспорт в ADIF.");
        var fields = tables[selected];
        string[] wanted = { "Call", "BandADIF", "Mode", "SubMode", "StationCallsign",
            "DateQSO", "TimeQSO", "DateQSO_Off", "TimeQSO_Off", "SWL", "WWLoc", "CNTY", "MyLocator" };
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT " + String.Join(",", wanted.Where(fields.Contains).Select(column => "[" + column + "]"))
                + " FROM [" + selected + "]";
            using (var reader = command.ExecuteReader())
            {
                var indexes = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, index => index, StringComparer.OrdinalIgnoreCase);
                while (reader.Read())
                {
                    string swl = Text(reader, indexes, "SWL");
                    if (swl == "1" || String.Equals(swl, "Y", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(swl, "True", StringComparison.OrdinalIgnoreCase)) continue;
                    string submode = Text(reader, indexes, "SubMode");
                    Console.WriteLine(String.Join("\t", new[] {
                        Text(reader, indexes, "Call"), Text(reader, indexes, "BandADIF"),
                        submode.Length == 0 ? Text(reader, indexes, "Mode") : submode,
                        Text(reader, indexes, "StationCallsign"),
                        Text(reader, indexes, "DateQSO", "yyyyMMdd"), Text(reader, indexes, "TimeQSO", "HHmmss"),
                        Text(reader, indexes, "DateQSO_Off", "yyyyMMdd"), Text(reader, indexes, "TimeQSO_Off", "HHmmss"),
                        Text(reader, indexes, "WWLoc"), Text(reader, indexes, "CNTY"), Text(reader, indexes, "MyLocator")
                    }));
                }
            }
        }
    }

    static string Text(OleDbDataReader reader, Dictionary<string, int> indexes, string field, string format = null)
    {
        int index;
        if (!indexes.TryGetValue(field, out index) || reader.IsDBNull(index)) return "";
        object value = reader.GetValue(index);
        if (value is DateTime && format != null) return ((DateTime)value).ToString(format, CultureInfo.InvariantCulture);
        return Convert.ToString(value, CultureInfo.InvariantCulture).Trim().Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
