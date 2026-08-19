using System;
using System.Collections.Generic;
using System.Text;
using Shared.Models;

namespace Shared.Database
{
    public abstract class SimpleCommand : IDisposable
    {
        protected MySqlCommand _mc;
        protected Dictionary<string, object> _set;

        protected SimpleCommand(string command, MySqlConnection conn, MySqlTransaction trans = null)
        {
            _mc = new MySqlCommand(command, conn, trans);
            _set = new Dictionary<string, object>();
        }

        public void Dispose()
        {
            _mc.Dispose();
        }

        public void AddParameter(string name, object value)
        {
            _mc.Parameters.AddWithValue(name, value);
        }

        public void Set(string field, object value)
        {
            _set[field] = value;
        }

        public abstract int Execute();
    }

    public class UpdateCommand : SimpleCommand
    {
        public UpdateCommand(string command, MySqlConnection conn, MySqlTransaction trans = null)
            : base(command, conn, trans)
        {
        }

        public override int Execute()
        {
            var sb = new StringBuilder();
            foreach (var parameter in _set.Keys)
                sb.AppendFormat("[{0}] = @{0}, ", parameter);

            _mc.CommandText = string.Format(_mc.CommandText, sb.ToString().Trim(' ', ','));

            foreach (var parameter in _set)
                _mc.Parameters.AddWithValue("@" + parameter.Key, parameter.Value);

            return _mc.ExecuteNonQuery();
        }
    }

    public class InsertCommand : SimpleCommand
    {
        public InsertCommand(string command, MySqlConnection conn, MySqlTransaction transaction = null)
            : base(command, conn, transaction)
        {
        }

        public long LastId => _mc.LastInsertedId;

        public override int Execute()
        {
            var sb1 = new StringBuilder();
            var sb2 = new StringBuilder();
            foreach (var parameter in _set.Keys)
            {
                sb1.AppendFormat("[{0}], ", parameter);
                sb2.AppendFormat("@{0}, ", parameter);
            }

            var values = "(" + sb1.ToString().Trim(' ', ',') + ") VALUES (" + sb2.ToString().Trim(' ', ',') + ")";
            _mc.CommandText = string.Format(_mc.CommandText, values);

            foreach (var parameter in _set)
                _mc.Parameters.AddWithValue("@" + parameter.Key, parameter.Value);

            return _mc.ExecuteNonQuery();
        }
    }
}

// Legacy source files still contain `using MySql.Data.MySqlClient;`.
// Keep the namespace available without exposing MySQL connection types.
// The real compatibility connection/command classes are in Shared.Models
// and are backed by Microsoft SQL Server.
namespace MySql.Data.MySqlClient
{
    internal static class SqlServerCompatibilityMarker
    {
    }
}
