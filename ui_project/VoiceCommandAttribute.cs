using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ui_project
{
    /// <summary>
    /// Marks a method as being a voice command.
    /// </summary>
    class VoiceCommandAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="VoiceCommandAttribute" /> class.
        /// </summary>
        /// <param name="tag">The tag.</param>
        /// <param name="items">The items.</param>
        public VoiceCommandAttribute(string tag, params string[] items)
        {
            this.Tag = tag;
            this.Items = new Collection<string>(items.ToList());
        }

        /// <summary>
        /// Gets the tag.
        /// </summary>
        /// <value>
        /// The tag.
        /// </value>
        public string Tag { get; private set; }

        /// <summary>
        /// Gets the items.
        /// </summary>
        /// <value>
        /// The items.
        /// </value>
        public ICollection<string> Items { get; private set; }
    }
}
