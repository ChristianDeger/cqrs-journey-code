﻿// ==============================================================================================================
// Microsoft patterns & practices
// CQRS Journey project
// ==============================================================================================================
// ©2012 Microsoft. All rights reserved. Certain content used with permission from contributors
// http://cqrsjourney.github.com/contributors/members
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance 
// with the License. You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software distributed under the License is 
// distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. 
// See the License for the specific language governing permissions and limitations under the License.
// ==============================================================================================================

namespace Conference
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.Entity;
    using System.Linq;
    using Common;

    public class ConferenceService
    {
        // TODO: transactionally save to DB the outgoing events
        // and an async process should pick and push to the bus.

        // using (tx)
        // {
        //  DB save (state snapshot)
        //  DB queue (events) -> push to bus (async)
        // }
        private IEventBus eventBus;
        private string nameOrConnectionString;

        public ConferenceService(IEventBus eventBus, string nameOrConnectionString = "ConferenceManagement")
        {
            this.eventBus = eventBus;
            this.nameOrConnectionString = nameOrConnectionString;
        }

        public Guid CreateConference(ConferenceInfo conference)
        {
            using (var context = new ConferenceContext(this.nameOrConnectionString))
            {
                var existingSlug = context.Conferences
                    .Where(c => c.Slug == conference.Slug)
                    .Select(c => c.Slug)
                    .Any();

                if (existingSlug)
                    throw new DuplicateNameException("The chosen conference slug is already taken.");

                conference.Id = Guid.NewGuid();
                context.Conferences.Add(conference);
                context.SaveChanges();

                this.PublishConferenceEvent<ConferenceCreated>(conference);
                foreach (var seat in conference.Seats)
                {
                    this.PublishSeatCreated(seat);
                }

                return conference.Id;
            }
        }

        public Guid CreateSeat(Guid conferenceId, SeatInfo seat)
        {
            using (var context = new ConferenceContext(this.nameOrConnectionString))
            {
                var conference = context.Conferences.Find(conferenceId);
                if (conference == null)
                    throw new ObjectNotFoundException();

                seat.Id = Guid.NewGuid();
                conference.Seats.Add(seat);
                context.SaveChanges();

                this.PublishSeatCreated(seat);

                return seat.Id;
            }
        }

        public ConferenceInfo FindConference(string slug)
        {
            using (var context = new ConferenceContext(this.nameOrConnectionString))
            {
                return context.Conferences.FirstOrDefault(x => x.Slug == slug);
            }
        }

        public ConferenceInfo FindConference(string email, string accessCode)
        {
            using (var context = new ConferenceContext(this.nameOrConnectionString))
            {
                return context.Conferences.FirstOrDefault(x => x.OwnerEmail == email && x.AccessCode == accessCode);
            }
        }

        public IEnumerable<SeatInfo> FindSeats(Guid conferenceId)
        {
            using (var context = new ConferenceContext(this.nameOrConnectionString))
            {
                return context.Conferences.Include(x => x.Seats)
                    .Where(x => x.Id == conferenceId)
                    .Select(x => x.Seats)
                    .FirstOrDefault() ??
                    Enumerable.Empty<SeatInfo>();
            }
        }

        public SeatInfo FindSeat(Guid seatId)
        {
            using (var context = new ConferenceContext(this.nameOrConnectionString))
            {
                return context.Seats.Find(seatId);
            }
        }

        public void UpdateConference(ConferenceInfo conference)
        {
            using (var context = new ConferenceContext(this.nameOrConnectionString))
            {
                var existing = context.Conferences.Find(conference.Id);
                if (existing == null)
                    throw new ObjectNotFoundException();

                context.Entry(existing).CurrentValues.SetValues(conference);
                context.SaveChanges();

                this.PublishConferenceEvent<ConferenceUpdated>(conference);
            }
        }

        public void UpdateSeat(Guid conferenceId, SeatInfo seat)
        {
            using (var context = new ConferenceContext(this.nameOrConnectionString))
            {
                var existing = context.Seats.Find(seat.Id);
                if (existing == null)
                    throw new ObjectNotFoundException();

                var diff = seat.Quantity - existing.Quantity;
                var e = diff > 0 ?
                    (IEvent)new SeatsAdded
                    {
                        ConferenceId = conferenceId,
                        SourceId = seat.Id,
                        AddedQuantity = diff,
                        TotalQuantity = seat.Quantity
                    } :
                    (IEvent)new SeatsRemoved
                    {
                        ConferenceId = conferenceId,
                        SourceId = seat.Id,
                        RemovedQuantity = Math.Abs(diff),
                        TotalQuantity = seat.Quantity
                    };

                context.Entry(existing).CurrentValues.SetValues(seat);
                context.SaveChanges();

                this.eventBus.Publish(e);
            }
        }

        public void UpdatePublished(Guid conferenceId, bool isPublished)
        {
            using (var context = new ConferenceContext(this.nameOrConnectionString))
            {
                var conference = context.Conferences.Find(conferenceId);
                if (conference == null)
                    throw new ObjectNotFoundException();

                conference.IsPublished = isPublished;
                if (isPublished && !conference.WasEverPublished)
                    // This flags prevents any further seat type deletions.
                    conference.WasEverPublished = true;

                context.SaveChanges();

                if (isPublished)
                    this.eventBus.Publish(new ConferencePublished { SourceId = conferenceId });
                else
                    this.eventBus.Publish(new ConferenceUnpublished { SourceId = conferenceId });
            }
        }

        public void DeleteSeat(Guid id)
        {
            using (var context = new ConferenceContext(this.nameOrConnectionString))
            {
                var seat = context.Seats.Find(id);
                if (seat == null)
                    throw new ObjectNotFoundException();

                var wasPublished = context.Conferences
                    .Where(x => x.Seats.Any(s => s.Id == id))
                    .Select(x => x.WasEverPublished)
                    .FirstOrDefault();

                if (wasPublished)
                    throw new InvalidOperationException("Can't delete seats from a conference that has been published at least once.");

                context.Seats.Remove(seat);
                context.SaveChanges();
            }
        }

        private void PublishConferenceEvent<T>(ConferenceInfo conference)
            where T : ConferenceEvent, new()
        {
            this.eventBus.Publish(new T()
            {
                SourceId = conference.Id,
                Owner = new Owner
                {
                    Name = conference.OwnerName,
                    Email = conference.OwnerEmail,
                },
                Name = conference.Name,
                Description = conference.Description,
                Slug = conference.Slug,
                StartDate = conference.StartDate,
                EndDate = conference.EndDate,
            });
        }

        private void PublishSeatCreated(SeatInfo seat)
        {
            this.eventBus.Publish(new SeatCreated
            {
                SourceId = seat.Id,
                Name = seat.Name,
                Description = seat.Description,
                Price = seat.Price,
            });
            this.eventBus.Publish(new SeatsAdded
            {
                SourceId = seat.Id,
                AddedQuantity = seat.Quantity,
                TotalQuantity = seat.Quantity,
            });
        }
    }
}
