using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;

namespace NLog.Raven
{
    public class RavenDataBag : DynamicObject
    {
        private readonly ConcurrentDictionary<string, dynamic> _properties =
            new ConcurrentDictionary<string, dynamic>(StringComparer.InvariantCultureIgnoreCase);

        public dynamic this[string key]
        {
            get => _properties.ContainsKey(key) ? _properties[key] : null;
            set => _properties[key] = value;
        }

        public override bool TryGetMember(GetMemberBinder binder, out dynamic result)
        {
            result = this._properties.ContainsKey(binder.Name) ? this._properties[binder.Name] : null;

            return true;
        }

        public override bool TrySetMember(SetMemberBinder binder, dynamic value)
        {
            if (value == null)
            {
                if (_properties.ContainsKey(binder.Name))
                    _properties.TryRemove(binder.Name, out _);
            }
            else
                _properties[binder.Name] = value;

            return true;
        }

        public override IEnumerable<string> GetDynamicMemberNames()
        {
            return _properties.Keys;
        }
    }

}