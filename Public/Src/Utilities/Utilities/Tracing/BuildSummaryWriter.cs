using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading.Tasks;

namespace BuildXL.Utilities.Tracing
{
    public class BuildSummaryWriter
    {
        private TextWriter m_writer;

        public BuildSummaryWriter()
        {

        }

        public void WriteHeader(string header)
        {
            m_writer.Write("### ");
            m_writer.WriteLine(header);
        }




        // Detailed table entries

        public void WriteDetailedTableEntry(string key, string value)
        {
            StartDetailedTableEntry(key);
            m_writer.WriteLine(HtmlEscape(value));
            EndDetailedTableEntry();
        }

        private void StartDetailedTableEntry(string key)
        {
            m_writer.WriteLine("<tr>");
            m_writer.WriteLine("<th>");
            m_writer.WriteLine(HtmlEscape(key));
            m_writer.WriteLine("</th>");
            m_writer.WriteLine("<td>");
        }

        private void EndDetailedTableEntry()
        {
            m_writer.WriteLine("</td>");
            m_writer.WriteLine("</tr>");
        }

        public void WriteDetailedTableSummary(string key, string summary, string expandedDetails)
        {
            StartDetailedTableSummary(key, summary);
            m_writer.WriteLine(HtmlEscape(expandedDetails));
            EndDetailedTableSummary();
        }

        public void StartDetailedTableSummary(string key, string summary)
        {
            StartDetailedTableEntry(key);
            m_writer.WriteLine("<details>");
            m_writer.WriteLine("<summary>");
            m_writer.WriteLine(HtmlEscape(summary));
            m_writer.WriteLine("</summary>");
        }

        public void EndDetailedTableSummary()
        {
            m_writer.WriteLine("</details>");
            EndDetailedTableEntry();
        }

        // Generic html elements

        public void WritePre(string contents)
        {
            m_writer.WriteLine("<pre>");
            m_writer.WriteLine(HtmlEscape(contents));
            m_writer.WriteLine("</pre>");
        }


        //
        // Generic table writers
        //

        public void StartTable(params object[] headers)
        {
            m_writer.WriteLine("<table>");
            if (headers?.Length > 0)
            {
                WriteRow(headers, true);
            }
        }

        public void WriteTableRow(params object[] columns)
        {
            WriteRow(columns, false);
        }

        private void WriteRow(object[] columns, bool isHeader)
        {
            var rowChar = isHeader ? 'h' : 'd';

            m_writer.WriteLine("<tr>");
            foreach (var column in columns)
            {
                m_writer.Write($"<t{rowChar}>");
                m_writer.Write(HtmlEscape(Convert.ToString(column, CultureInfo.InvariantCulture)));
                m_writer.Write($"</t{rowChar}>");
            }
            m_writer.WriteLine("</tr>");

        }

        public void EndTable()
        {
            m_writer.WriteLine("</table>");
        }






        private string HtmlEscape(string value)
        {
            return WebUtility.HtmlEncode(value);
        }
    }
}
