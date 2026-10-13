using System;
using System.Collections.Generic;
using System.Drawing;
using CefSharp;
using CefSharp.Enums;

namespace RainbowMage.HtmlRenderer
{
    class DragHandler : IDragHandler
    {
        Renderer renderer;

        public DragHandler(Renderer form)
        {
            this.renderer = form;
        }

        public bool OnDragEnter(IWebBrowser chromiumWebBrowser, IBrowser browser, IDragData dragData, DragOperationsMask mask)
        {
            return false;
        }

        public void OnDraggableRegionsChanged(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IList<DraggableRegion> regions)
        {
            // Fork: an exception thrown back into CEF ends ACT (seen as a NullReferenceException
            // here while an overlay page loaded at ACT's start).
            try
            {
                UpdateRegion(frame, regions);
            }
            catch (Exception)
            {
            }
        }

        private void UpdateRegion(IFrame frame, IList<DraggableRegion> regions)
        {
            if (frame == null || regions == null || !frame.IsValid || !frame.IsMain) return;

            var region = new Region();
            region.MakeEmpty();

            foreach (var sub in regions)
            {
                var rect = new Rectangle(sub.X, sub.Y, sub.Width, sub.Height);

                if (sub.Draggable)
                {
                    region.Union(rect);
                }
                else
                {
                    region.Exclude(rect);
                }
            }

            renderer.DraggableRegion = region;
        }
    }
}
