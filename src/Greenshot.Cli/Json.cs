/*
 * Greenshot - a free and open source screenshot tool
 * Copyright (C) 2007-2026 Thomas Braun, Jens Klingen, Robin Krom
 *
 * For more information see: https://getgreenshot.org/
 * The Greenshot project is hosted on GitHub https://github.com/greenshot/greenshot
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 1 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Greenshot.Cli;

/// <summary>
/// Just enough JSON writing for the CLI output, so single line JSON can be produced without another dependency
/// </summary>
internal static class Json
{
    public static string String(string value)
    {
        if (value == null)
        {
            return "null";
        }
        var text = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        text.Append("\\u").Append(((int) c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        text.Append(c);
                    }
                    break;
            }
        }
        return text.Append('"').ToString();
    }

    public static string Bool(bool value) => value ? "true" : "false";

    public static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Rectangle as {"x":..,"y":..,"width":..,"height":..}</summary>
    public static string Rect(Rectangle rect) =>
        $"{{\"x\":{rect.X},\"y\":{rect.Y},\"width\":{rect.Width},\"height\":{rect.Height}}}";

    /// <summary>Object from name and already serialized value pairs, nulls are left out</summary>
    public static string Object(params (string Name, string Value)[] members) =>
        "{" + string.Join(",", members.Where(m => m.Value != null).Select(m => String(m.Name) + ":" + m.Value)) + "}";

    public static string Array(IEnumerable<string> values) => "[" + string.Join(",", values) + "]";
}
