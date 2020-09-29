using System.ComponentModel;
using NLog.Config;
using NLog.Layouts;

namespace NLog.Raven
{
    /// <summary>
    /// A configuration item for RavenDB target.
    /// </summary>
    [NLogConfigurationItem]
    [ThreadAgnostic]
    public sealed class RavenField
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RavenField"/> class.
        /// </summary>
        public RavenField()
            : this(null, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RavenField" /> class.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="layout">The layout.</param>
        public RavenField(string name, Layout layout)
        {
            Name = name;
            Layout = layout;
        }

        /// <summary>
        /// Gets or sets the name of the RavenDB field.
        /// </summary>
        /// <value>
        /// The name of the RavenDB field.
        /// </value>
        [RequiredParameter]
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the layout used to generate the value for the field.
        /// </summary>
        /// <value>
        /// The layout used to generate the value for the field.
        /// </value>
        [RequiredParameter]
        public Layout Layout { get; set; }


    }
}